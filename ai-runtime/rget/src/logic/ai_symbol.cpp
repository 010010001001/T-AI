// src/logic/ai_symbol.cpp
#include "logic_engine.h"
#include "sympy_engine.h"
#include "json_util.h"

#include <cstdlib>
#include <string>

extern "C" {

void ai_free_string(char* s) { std::free(s); }

int ai_symbol_validate(const char* expr_utf8, char** result_json)
{
    if (!expr_utf8 || !result_json) return 1;

    char* normalized = nullptr;
    char* error = nullptr;
    int rc = sympy_validate(expr_utf8, &normalized, &error);

    std::string out;
    if (rc == 0 && normalized)
    {
        out = "{\"valid\":true,\"normalized\":" + ai_json::quote(normalized) + "}";
    }
    else
    {
        out = "{\"valid\":false,\"error\":" + ai_json::quote(error ? error : "unknown") + "}";
    }

    *result_json = ai_json::strdup_alloc(out);

    if (normalized) sympy_free_string(normalized);
    if (error) sympy_free_string(error);

    return *result_json ? 0 : 1;
}

int ai_symbol_execute(const char* expr_utf8, char** result_json)
{
    if (!expr_utf8 || !result_json) return 1;

    char* value = nullptr;
    char* proof = nullptr;
    char* error = nullptr;

    int rc = sympy_execute(expr_utf8, &value, &proof, &error);

    const char* status = (rc == 0) ? "OK" : (rc == 1 ? "UNSUPPORTED" : "ERROR");

    std::string out = std::string("{\"status\":\"") + status + "\"";
    if (value) out += ",\"value\":" + ai_json::quote(value);
    if (proof) out += ",\"proof\":" + ai_json::quote(proof);
    if (error) out += ",\"error\":" + ai_json::quote(error);
    out += "}";

    *result_json = ai_json::strdup_alloc(out);

    if (value) sympy_free_string(value);
    if (proof) sympy_free_string(proof);
    if (error) sympy_free_string(error);

    return *result_json ? 0 : 1;
}

} // extern "C"
