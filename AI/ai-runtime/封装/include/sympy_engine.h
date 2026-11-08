#pragma once
#ifdef __cplusplus
extern "C" {
#endif

// 表达式语法校验：0=有效，非0=无效
// normalized_utf8：规范化的表达式文本（调用方 ai_free_string 释放）
int sympy_validate(const char* expr_utf8, char** normalized_utf8, char** error_utf8);

// 表达式求值：0=成功，1=UNSUPPORTED（构建时未覆盖的表达式类型），2=ERROR
// result_utf8：数值或符号结果；proof_utf8：可选的化简/证明步骤
int sympy_execute(const char* expr_utf8,
                  char** result_utf8, char** proof_utf8, char** error_utf8);

void ai_free_string(char* s);

#ifdef __cplusplus
}
#endif