/* include/logic_engine.h
 *
 * logic_engine.dll 的公开 C ABI。
 *
 * 内存所有权：本文件声明的所有 char** 输出参数（以及 ai_free_string 的入参）
 * 均由 logic_engine.dll 内的 malloc/strdup 分配，调用方必须用本 DLL 导出的
 * ai_free_string 释放。不可用 sympy_engine.dll 的 sympy_free_string，也不可
 * 用 ai_inference.dll 的同名 ai_free_string——每个 DLL 有自己的分配器。
 *
 * 错误码约定：
 *   返回 0 表示函数执行完毕，结果在 *result_json 中；
 *     即使 JSON 内的 status 是 "ERROR" 或 "UNSUPPORTED"，仍返回 0。
 *   返回非 0 表示函数本身调用失败（参数为空、内部 OOM），
 *     此时 *result_json 可能未写入，调用方不得解析它。
 */
#ifndef LOGIC_ENGINE_H
#define LOGIC_ENGINE_H

#ifdef _WIN32
  #ifdef LOGIC_ENGINE_EXPORTS
    #define LOGIC_API __declspec(dllexport)
  #else
    #define LOGIC_API __declspec(dllimport)
  #endif
#else
  #define LOGIC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* --------------------------------------------------------------------------
 * 意图路由（确定性规则，不经过 LLM）
 * 输出 JSON: {"route":"SYMBOLIC|TOOL|OPEN","confidence":1.0}
 * -------------------------------------------------------------------------- */
LOGIC_API int ai_route(const char* question_utf8, char** result_json);

/* --------------------------------------------------------------------------
 * 符号表达式语法校验
 * 输出 JSON: {"valid":true,"normalized":"..."}
 *        或: {"valid":false,"error":"..."}
 * 校验失败仍返回 0；返回值非 0 仅表示函数本身调用失败。
 * -------------------------------------------------------------------------- */
LOGIC_API int ai_symbol_validate(const char* expr_utf8, char** result_json);

/* --------------------------------------------------------------------------
 * 符号运算（调用 sympy_engine.dll 的模板）
 * 输出 JSON: {"status":"OK|UNSUPPORTED|ERROR","value":"...","proof":"..."}
 * -------------------------------------------------------------------------- */
LOGIC_API int ai_symbol_execute(const char* expr_utf8, char** result_json);

/* --------------------------------------------------------------------------
 * 确定性关键字提取（不调用 LLM）
 * 输出 JSON: {"keywords":["..."],"summary":"..."}
 * -------------------------------------------------------------------------- */
LOGIC_API int ai_extract_keywords(const char* text_utf8, char** result_json);

/* --------------------------------------------------------------------------
 * 开放域一致性校验（形式化矛盾检测）
 * 输出 JSON: {"consistent":true|false,"conflicts":["..."]}
 * -------------------------------------------------------------------------- */
LOGIC_API int ai_consistency_check(const char* text_utf8, char** result_json);

/* --------------------------------------------------------------------------
 * LLM 候选表达式的符号引擎裁决
 * 输出 JSON: {"accepted":true,"value":"...","proof":"..."}
 *        或: {"accepted":false,"reason":"syntax_invalid|unsupported|execution_error",
 *             "detail":"..."}
 * accepted:true 时返回的 value 来自符号引擎，不是 LLM 的候选数值。
 * -------------------------------------------------------------------------- */
LOGIC_API int ai_verify_candidate(const char* candidate_utf8, char** result_json);

/* --------------------------------------------------------------------------
 * 释放本 DLL 分配的字符串。
 * 与 sympy_engine.dll 的 sympy_free_string、ai_inference.dll 的 ai_free_string
 * 是三个独立实现，不可交叉调用。
 * -------------------------------------------------------------------------- */
LOGIC_API void ai_free_string(char* s);

#ifdef __cplusplus
}
#endif

#endif /* LOGIC_ENGINE_H */