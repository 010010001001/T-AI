// src/logic/ai_verify.cpp
#include "logic_engine.h"
#include "sympy_engine.h"
#include "json_util.h"

#include <cstdlib>
#include <string>

extern "C" {

int ai_verify_candidate(const char* candidate_utf8, char** result_json)
{
    if (!candidate_utf8 || !result_json) return 1;

    // 步骤 1：语法校验。校验失败不是 API 错误，结果由 JSON 表达。
    char* normalized = nullptr;
    char* verr = nullptr;
    int vrc = sympy_validate(candidate_utf8, &normalized, &verr);

    if (vrc != 0)
    {
        std::string out =
            "{\"accepted\":false,\"reason\":\"syntax_invalid\",\"detail\":" +
            ai_json::quote(verr ? verr : "") + "}";
        *result_json = ai_json::strdup_alloc(out);
        if (normalized) sympy_free_string(normalized);
        if (verr) sympy_free_string(verr);
        return *result_json ? 0 : 1;
    }

    // 步骤 2：符号运算。最终值只来自符号引擎，不采信 LLM 给出的数值。
    char* value = nullptr;
    char* proof = nullptr;
    char* eerr = nullptr;
    int erc = sympy_execute(normalized, &value, &proof, &eerr);

    if (normalized) sympy_free_string(normalized);

    std::string out;
    if (erc == 0)
    {
        out = "{\"accepted\":true,\"value\":" +
              ai_json::quote(value ? value : "") +
              ",\"proof\":" + ai_json::quote(proof ? proof : "") + "}";
    }
    else
    {
        out = std::string("{\"accepted\":false,\"reason\":\"") +
              (erc == 1 ? "unsupported" : "execution_error") +
              "\",\"detail\":" + ai_json::quote(eerr ? eerr : "") + "}";
    }

    *result_json = ai_json::strdup_alloc(out);

    if (value) sympy_free_string(value);
    if (proof) sympy_free_string(proof);
    if (eerr) sympy_free_string(eerr);

    return *result_json ? 0 : 1;
}

} // extern "C"
