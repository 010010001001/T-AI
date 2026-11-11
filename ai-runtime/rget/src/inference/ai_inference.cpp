// src/inference/ai_inference.cpp
//
// ai_inference.dll：C++ 推理引擎，仅暴露"整批"接口给 C# 层：
//   ai_backend_init / ai_model_load / ai_model_loaded / ai_generate / ai_count_tokens
// token 生成循环全部留在原生侧；C# 只通过流式回调接收已生成文本片段。
//
// 依赖 llama.cpp（建议 b3800 及以上；使用 llama_model_get_vocab / llama_sampler_chain API）。
// GGML 后端（CUDA / CPU）通过 ai_backend_init 指定目录动态加载。

#include "ai_inference.h"

#include "ggml-backend.h"
#include "llama.h"

#include <algorithm>
#include <cctype>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

std::mutex g_mutex;
llama_model* g_model = nullptr;
const llama_vocab* g_vocab = nullptr;
bool g_backend_ready = false;

/* 进程级唯一上下文：C# 层已用 SemaphoreSlim 串行化调用，这里再加一层防御。 */
struct ContextGuard
{
    std::lock_guard<std::mutex> lock;
    explicit ContextGuard() : lock(g_mutex) {}
};

std::string json_escape(const std::string& in)
{
    std::string out;
    out.reserve(in.size() + 8);
    for (unsigned char c : in)
    {
        switch (c)
        {
        case '"':  out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n";  break;
        case '\r': out += "\\r";  break;
        case '\t': out += "\\t";  break;
        default:
            if (c < 0x20)
            {
                char buf[8];
                std::snprintf(buf, sizeof(buf), "\\u%04X", static_cast<unsigned>(c));
                out += buf;
            }
            else
            {
                out.push_back(static_cast<char>(c));
            }
        }
    }
    return out;
}

char* make_error(const std::string& message)
{
    std::string json = "{\"error\":\"" + json_escape(message) + "\"}";
    char* p = static_cast<char*>(std::malloc(json.size() + 1));
    if (!p) return nullptr;
    std::memcpy(p, json.c_str(), json.size() + 1);
    return p;
}

/* 读取 params_json 中的数字字段；找不到时返回 fallback。 */
int read_int_param(const char* json, const char* key, int fallback)
{
    if (!json) return fallback;
    std::string needle = std::string("\"") + key + "\"";
    const char* p = std::strstr(json, needle.c_str());
    if (!p) return fallback;
    p += needle.size();
    while (*p && *p != ':' && *p != '\0') ++p;
    if (*p == ':') ++p;
    while (*p && std::isspace(static_cast<unsigned char>(*p))) ++p;
    if (!*p) return fallback;
    return std::atoi(p);
}

float read_float_param(const char* json, const char* key, float fallback)
{
    if (!json) return fallback;
    std::string needle = std::string("\"") + key + "\"";
    const char* p = std::strstr(json, needle.c_str());
    if (!p) return fallback;
    p += needle.size();
    while (*p && *p != ':' && *p != '\0') ++p;
    if (*p == ':') ++p;
    while (*p && std::isspace(static_cast<unsigned char>(*p))) ++p;
    if (!*p) return fallback;
    return static_cast<float>(std::atof(p));
}

int env_int(const char* name, int fallback)
{
    const char* v = std::getenv(name);
    return v ? std::atoi(v) : fallback;
}

/* 统计一个 UTF-8 前缀中完整的字符边界，返回可安全发送的字节数。 */
size_t utf8_complete_prefix(const std::string& s)
{
    size_t i = 0;
    size_t lastComplete = 0;

    while (i < s.size())
    {
        unsigned char c = static_cast<unsigned char>(s[i]);
        size_t len = 1;

        if (c < 0x80) len = 1;
        else if ((c & 0xE0) == 0xC0) len = 2;
        else if ((c & 0xF0) == 0xE0) len = 3;
        else if ((c & 0xF8) == 0xF0) len = 4;
        else { ++i; continue; }   // 非法起始字节，跳过

        if (i + len > s.size()) break;   // 不完整序列，留给下一次

        bool valid = true;
        for (size_t k = 1; k < len; ++k)
        {
            unsigned char cc = static_cast<unsigned char>(s[i + k]);
            if ((cc & 0xC0) != 0x80) { valid = false; break; }
        }

        i += len;
        if (valid) lastComplete = i;
        else break;
    }

    return lastComplete;
}

int tokenize(const char* text, std::vector<llama_token>* out, bool addSpecial, bool parseSpecial)
{
    if (!g_vocab || !text) return -1;

    // 先查询所需 token 数：容量为 0 时 llama_tokenize 返回负的所需数量
    int n = llama_tokenize(g_vocab, text, static_cast<int32_t>(std::strlen(text)),
                           nullptr, 0, addSpecial, parseSpecial);
    if (n == 0) return 0;

    int count = n > 0 ? n : -n;
    out->resize(static_cast<size_t>(count));

    int written = llama_tokenize(g_vocab, text, static_cast<int32_t>(std::strlen(text)),
                                 out->data(), count, addSpecial, parseSpecial);
    if (written < 0)
    {
        out->clear();
        return -1;
    }
    out->resize(static_cast<size_t>(written));
    return written;
}

/* 用模型自带的 chat template 包装单条 user 消息。
 * AVA/Qwen 是对话微调模型，裸文本会触发退化输出（<tool_call> 重复、<think> 泄漏）。 */
static bool apply_chat_template(const char* user_prompt, std::string* out)
{
    const char* tmpl = llama_model_chat_template(g_model, nullptr);
    if (!tmpl || !*tmpl) return false;

    llama_chat_message msgs[2];
    msgs[0].role = "system";
    msgs[0].content = "你是本地部署的助手。直接回答用户；不要输出工具调用，不要输出 <tool_call>、<think>、<|im_end|> 等标签。";
    msgs[1].role = "user";
    msgs[1].content = user_prompt;

    int nMsg = 2;
    int need = llama_chat_apply_template(tmpl, msgs, nMsg, true, nullptr, 0);
    if (need <= 0)
    {
        // 某些模板不接受 system 角色，退化为单条 user 消息
        nMsg = 1;
        need = llama_chat_apply_template(tmpl, &msgs[1], 1, true, nullptr, 0);
        if (need <= 0) return false;
    }

    std::vector<char> buffer(static_cast<size_t>(need) + 1);
    int written = llama_chat_apply_template(tmpl, nMsg == 2 ? msgs : &msgs[1], nMsg, true,
                                            buffer.data(), static_cast<int32_t>(buffer.size()));
    if (written <= 0) return false;

    out->assign(buffer.data(), static_cast<size_t>(written));
    return true;
}

} // namespace

// ai_backend_init 与 ai_model_load 都可能触发后端初始化；调用方持有 g_mutex，
// 因此这里必须是"已加锁"版本的内部函数，避免 std::mutex 自锁死。
static int ensure_backend_locked(const char* backends_dir)
{
    if (g_backend_ready) return 0;

    llama_backend_init();

    if (backends_dir && *backends_dir)
        ggml_backend_load_all_from_path(backends_dir);
    else
        ggml_backend_load_all();

    g_backend_ready = true;
    return 0;
}

// ============================================================================
// C ABI
// ============================================================================

extern "C" {

int ai_backend_init(const char* backends_dir)
{
    ContextGuard guard;
    return ensure_backend_locked(backends_dir);
}

int ai_model_load(const char* gguf_path, char** error_json)
{
    ContextGuard guard;

    if (!gguf_path || !*gguf_path)
    {
        if (error_json) *error_json = make_error("gguf_path is empty");
        return 1;
    }

    if (!g_backend_ready) ensure_backend_locked(nullptr);

    if (g_model)
    {
        llama_model_free(g_model);
        g_model = nullptr;
        g_vocab = nullptr;
    }

    llama_model_params mp = llama_model_default_params();
    // 3060 8G 默认全部层上卡；若需要把显存压到 2GB 以内，
    // 通过环境变量设置 AI_GPU_LAYERS=12（或更低）改为部分卸载。
    mp.n_gpu_layers = env_int("AI_GPU_LAYERS", 99);

    g_model = llama_model_load_from_file(gguf_path, mp);
    if (!g_model)
    {
        if (error_json) *error_json = make_error("failed to load gguf");
        return 2;
    }

    g_vocab = llama_model_get_vocab(g_model);
    if (!g_vocab)
    {
        llama_model_free(g_model);
        g_model = nullptr;
        if (error_json) *error_json = make_error("failed to get vocab");
        return 3;
    }

    return 0;
}

int ai_model_loaded(void)
{
    ContextGuard guard;
    return g_model ? 1 : 0;
}

int ai_count_tokens(const char* text)
{
    ContextGuard guard;
    if (!g_vocab || !text) return -1;

    std::vector<llama_token> tokens;
    if (tokenize(text, &tokens, false, false) < 0) return -1;
    return static_cast<int>(tokens.size());
}

int ai_generate(const char* prompt, const char* params_json,
                ai_token_callback cb, void* user_data, char** error_json)
{
    ContextGuard guard;

    if (!g_model || !g_vocab) { if (error_json) *error_json = make_error("model not loaded"); return 1; }
    if (!prompt) { if (error_json) *error_json = make_error("prompt is null"); return 1; }

    int maxTokens = read_int_param(params_json, "max_tokens", 512);
    if (maxTokens <= 0) maxTokens = 512;
    if (maxTokens > 4096) maxTokens = 4096;

    int ctx = read_int_param(params_json, "ctx", 4096);
    if (ctx < 512) ctx = 512;
    if (ctx > 32768) ctx = 32768;

    float temperature = read_float_param(params_json, "temperature", 0.7f);
    int topK = read_int_param(params_json, "top_k", 40);
    float topP = read_float_param(params_json, "top_p", 0.9f);

    // 对话微调模型必须套用自带 chat template；没有模板时退回裸文本
    std::string templated;
    const char* effectivePrompt = prompt;
    if (apply_chat_template(prompt, &templated))
        effectivePrompt = templated.c_str();

    std::vector<llama_token> promptTokens;
    if (tokenize(effectivePrompt, &promptTokens, false, true) < 0)
    {
        if (error_json) *error_json = make_error("prompt tokenize failed");
        return 2;
    }

    if (static_cast<int>(promptTokens.size()) >= ctx - 4)
    {
        // 截断时保留尾部：最新的用户问题与 assistant 起始标记在末尾，
        // 丢掉头部（system/旧摘要）比丢掉问题更安全。
        int keep = std::max(64, ctx - maxTokens - 8);
        if (keep < static_cast<int>(promptTokens.size()))
        {
            size_t start = promptTokens.size() - static_cast<size_t>(keep);
            promptTokens.erase(promptTokens.begin(), promptTokens.begin() + static_cast<std::ptrdiff_t>(start));
        }
    }

    llama_context_params cp = llama_context_default_params();
    cp.n_ctx = static_cast<uint32_t>(ctx);
    cp.n_batch = 512;
    cp.n_ubatch = 512;
    cp.n_threads = static_cast<int32_t>(std::max(1u, std::thread::hardware_concurrency() / 2));
    cp.n_threads_batch = cp.n_threads;

    llama_context* ctxHandle = llama_init_from_model(g_model, cp);
    if (!ctxHandle)
    {
        if (error_json) *error_json = make_error("failed to create context");
        return 3;
    }

    // 采样器链：温度 <= 0 时退化为贪心，保证确定性路径可复现
    llama_sampler_chain_params sp = llama_sampler_chain_default_params();
    llama_sampler* sampler = llama_sampler_chain_init(sp);

    if (temperature <= 0.0f)
    {
        llama_sampler_chain_add(sampler, llama_sampler_init_greedy());
    }
    else
    {
        llama_sampler_chain_add(sampler, llama_sampler_init_top_k(topK > 0 ? topK : 40));
        llama_sampler_chain_add(sampler, llama_sampler_init_top_p(topP > 0 ? topP : 0.9f, 1));
        llama_sampler_chain_add(sampler, llama_sampler_init_temp(temperature));
        llama_sampler_chain_add(sampler, llama_sampler_init_dist(LLAMA_DEFAULT_SEED));
    }

    auto cleanup = [&]()
    {
        llama_sampler_free(sampler);
        llama_free(ctxHandle);
    };

    // 提示词一次性解码：位置 0..n-1，只有最后一个 token 需要 logits。
    // 使用 llama_batch_init 显式设置位置，避免不同 llama.cpp 版本中
    // llama_batch_get_one 对 pos 的隐含行为差异。
    const int nPrompt = static_cast<int>(promptTokens.size());
    const int nBatch = 512;
    llama_batch promptBatch = llama_batch_init(nBatch, 0, 1);

    for (int off = 0; off < nPrompt; off += nBatch)
    {
        int n = std::min(nBatch, nPrompt - off);

        for (int k = 0; k < n; ++k)
        {
            promptBatch.token[k] = promptTokens[static_cast<size_t>(off + k)];
            promptBatch.pos[k] = off + k;
            promptBatch.n_seq_id[k] = 1;
            promptBatch.seq_id[k][0] = 0;
            promptBatch.logits[k] = (off + k == nPrompt - 1);
        }
        promptBatch.n_tokens = n;

        int rc = llama_decode(ctxHandle, promptBatch);
        if (rc != 0)
        {
            llama_batch_free(promptBatch);
            cleanup();
            if (error_json) *error_json = make_error("llama_decode(prompt) failed");
            return 5;
        }
    }
    llama_batch_free(promptBatch);

    llama_batch tokenBatch = llama_batch_init(1, 0, 1);
    llama_pos nPast = static_cast<llama_pos>(nPrompt);

    std::string pending;
    int generated = 0;

    while (generated < maxTokens)
    {
        llama_token token = llama_sampler_sample(sampler, ctxHandle, -1);
        if (token < 0) break;

        if (llama_vocab_is_eog(g_vocab, token)) break;

        char piece[128];
        int len = llama_token_to_piece(g_vocab, token, piece, sizeof(piece), 0, false);
        if (len < 0) break;

        pending.append(piece, static_cast<size_t>(len));

        // 只把完整的 UTF-8 字符发给回调，避免 CJK 被 token 边界切断
        size_t complete = utf8_complete_prefix(pending);
        if (complete > 0 && cb)
        {
            std::string chunk = pending.substr(0, complete);
            cb(chunk.c_str(), user_data);
        }
        if (complete > 0) pending.erase(0, complete);

        ++generated;

        tokenBatch.token[0] = token;
        tokenBatch.pos[0] = nPast++;
        tokenBatch.n_seq_id[0] = 1;
        tokenBatch.seq_id[0][0] = 0;
        tokenBatch.logits[0] = true;
        tokenBatch.n_tokens = 1;

        if (llama_decode(ctxHandle, tokenBatch) != 0) break;
    }

    llama_batch_free(tokenBatch);

    if (!pending.empty() && cb)
        cb(pending.c_str(), user_data);

    cleanup();
    return 0;
}

void ai_free_string(char* s) { std::free(s); }

} // extern "C"
