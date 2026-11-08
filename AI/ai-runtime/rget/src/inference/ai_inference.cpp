#include "llama.h"
#include <string>
#include <vector>
#include <cstring>
#include <cstdlib>

// ---------- 全局状态：单模型单上下文，由 ai_backend_init 初始化 ----------
static llama_model*   g_model   = nullptr;
static llama_context* g_ctx     = nullptr;
static const llama_vocab* g_vocab = nullptr;
static int            g_n_past  = 0;

extern "C" {

void ai_free_string(char* s) { if (s) std::free(s); }

static char* dup_json(const char* s) {
    size_t n = std::strlen(s) + 1;
    char* p = (char*)std::malloc(n);
    if (p) std::memcpy(p, s, n);
    return p;
}

int ai_backend_init(const char* backends_dir_utf8) {
    // 加载 CUDA 后端：ggml_backend_load_all_from_path 会扫描目录并注册 ggml-cuda.dll
    ggml_backend_load_all_from_path(backends_dir_utf8);

    llama_backend_init();

    llama_model_params mp = llama_model_default_params();
    mp.n_gpu_layers = 99;          // MiniCPM5-2B Q4_K_M 全量上 GPU，显存约 1.56GB
    mp.use_mmap     = true;

    g_model = llama_model_load_from_file("models/MiniCPM5-2B-Q4_K_M.gguf", mp);
    if (!g_model) return 1;

    llama_context_params cp = llama_context_default_params();
    cp.n_ctx   = 4096;             // 3GB 预算下 4K 上下文的安全余量
    cp.n_batch = 512;
    g_ctx = llama_init_from_model(g_model, cp);
    if (!g_ctx) return 2;

    g_vocab = llama_model_get_vocab(g_model);
    g_n_past = 0;
    return 0;
}

int ai_generate(
    const char* prompt_utf8,
    const char* params_json,
    token_cb cb,
    void* user_data,
    char** error_json)
{
    if (!g_ctx) { *error_json = dup_json("{\"error\":\"not_initialized\"}"); return 1; }

    // --- 1. 将 prompt 分词并批量 decode（prompt 处理阶段） ---
    const int n_prompt_max = (int)std::strlen(prompt_utf8) + 16;
    std::vector<llama_token> tokens(n_prompt_max);
    int n_prompt = llama_tokenize(
        g_vocab, prompt_utf8, (int)std::strlen(prompt_utf8),
        tokens.data(), n_prompt_max,
        /*add_special=*/true, /*parse_special=*/true);
    if (n_prompt < 0) { *error_json = dup_json("{\"error\":\"tokenize_failed\"}"); return 2; }
    tokens.resize(n_prompt);

    llama_batch batch = llama_batch_get_one(tokens.data(), n_prompt);
    if (llama_decode(g_ctx, batch) != 0) { *error_json = dup_json("{\"error\":\"decode_prompt\"}"); return 3; }
    g_n_past = n_prompt;

    // --- 2. 构建采样器链：top_k -> top_p -> temp -> dist ---
    llama_sampler_chain_params sp = llama_sampler_chain_default_params();
    llama_sampler* sampler = llama_sampler_chain_init(sp);
    llama_sampler_chain_add(sampler, llama_sampler_init_top_k(40));
    llama_sampler_chain_add(sampler, llama_sampler_init_top_p(0.9f, 1));
    llama_sampler_chain_add(sampler, llama_sampler_init_temp(0.7f));
    llama_sampler_chain_add(sampler, llama_sampler_init_dist(LLAMA_DEFAULT_SEED));

    // --- 3. 自回归循环：采样 -> 回调 -> decode -> 直到 EOG 或 max_tokens ---
    const int max_tokens = 512;    // 从 params_json 解析，骨架中固定
    for (int i = 0; i < max_tokens; ++i) {
        llama_token id = llama_sampler_sample(sampler, g_ctx, -1);
        if (llama_vocab_is_eog(g_vocab, id)) break;

        char buf[256];
        int n = llama_token_to_piece(g_vocab, id, buf, sizeof(buf), 0, true);
        if (n > 0) {
            buf[n] = '\0';
            cb(buf, user_data);                 // 流式回调，C# 侧 Marshal 封送
        }

        llama_batch next = llama_batch_get_one(&id, 1);
        if (llama_decode(g_ctx, next) != 0) break;
        g_n_past++;
    }

    llama_sampler_free(sampler);
    return 0;
}

} // extern "C"

// 返回分词后的 token 数；失败返回 -1
int ai_count_tokens(const char* text_utf8);
extern "C" int ai_count_tokens(const char* text_utf8) {
    if (!g_vocab) return -1;
    int n = (int)std::strlen(text_utf8) + 16;
    std::vector<llama_token> buf(n);
    int rc = llama_tokenize(g_vocab, text_utf8, (int)std::strlen(text_utf8),
                            buf.data(), n, true, true);
    return rc < 0 ? -1 : rc;
}