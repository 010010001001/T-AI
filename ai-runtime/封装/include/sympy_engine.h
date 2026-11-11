#pragma once
#ifdef _WIN32
  #ifdef SYMPY_ENGINE_EXPORTS
    #define SYMPY_API __declspec(dllexport)
  #else
    #define SYMPY_API __declspec(dllimport)
  #endif
#else
  #define SYMPY_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

// 表达式语法校验：0=有效，非0=无效（1=不支持，2=语法错误）
// normalized_utf8：规范化的表达式文本（调用方 sympy_free_string 释放）
SYMPY_API int sympy_validate(const char* expr_utf8, char** normalized_utf8, char** error_utf8);

// 表达式求值：0=成功，1=UNSUPPORTED（构建时未覆盖的表达式类型），2=ERROR
// result_utf8：数值结果；proof_utf8：可选证明信息；error_utf8：失败原因
SYMPY_API int sympy_execute(const char* expr_utf8,
                            char** result_utf8, char** proof_utf8, char** error_utf8);

// 释放 sympy_engine.dll 分配的字符串。
// 与 logic_engine.dll / ai_inference.dll 的字符串分配器相互独立，不可交叉释放。
SYMPY_API void sympy_free_string(char* s);

#ifdef __cplusplus
}
#endif
