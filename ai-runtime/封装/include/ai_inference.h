/* include/ai_inference.h
 *
 * ai_inference.dll 的公开 C ABI（纯 C，供 C# P/Invoke 与 C 插件使用）。
 *
 * 内存所有权：本 DLL 分配的字符串（*error_json）必须用 ai_free_string 释放。
 * 与 logic_engine.dll / sympy_engine.dll / 插件的分配器相互独立，不可交叉释放。
 *
 * 线程模型：原生层只允许单线程生成；C# 层负责串行化。
 */
#ifndef AI_INFERENCE_H
#define AI_INFERENCE_H

#ifdef _WIN32
  #ifdef AI_INFERENCE_EXPORTS
    #define AI_INFER_API __declspec(dllexport)
  #else
    #define AI_INFER_API __declspec(dllimport)
  #endif
#else
  #define AI_INFER_API __attribute__((visibility("default")))
#endif

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* 流式回调：每次收到一个完整 UTF-8 片段。不得抛出异常/长跳转。 */
typedef void (*ai_token_callback)(const char* token_utf8, void* user_data);

/* 初始化 GGML 后端。backends_dir 为空时使用默认搜索路径。 */
AI_INFER_API int ai_backend_init(const char* backends_dir);

/* 加载 GGUF 模型并持有进程级模型句柄。重复调用会释放旧模型。 */
AI_INFER_API int ai_model_load(const char* gguf_path, char** error_json);

/* 返回 1 表示模型已加载。 */
AI_INFER_API int ai_model_loaded(void);

/* 流式生成。params_json 支持 max_tokens/temperature/top_k/top_p/ctx。 */
AI_INFER_API int ai_generate(const char* prompt,
                             const char* params_json,
                             ai_token_callback cb,
                             void* user_data,
                             char** error_json);

/* 精确 token 计数；模型未加载或失败返回 -1。 */
AI_INFER_API int ai_count_tokens(const char* text);

/* 释放本 DLL 分配的字符串。 */
AI_INFER_API void ai_free_string(char* s);

#ifdef __cplusplus
}
#endif

#endif /* AI_INFERENCE_H */
