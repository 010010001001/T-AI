#include "sympy_engine.h"
#include <cstring>
#include <cstdlib>
#include <string>

extern "C" {

int ai_symbol_validate(const char* expr_utf8, char** result_json) {
    char* normalized = nullptr;
    char* error = nullptr;
    int rc = sympy_validate(expr_utf8, &normalized, &error);
    if (rc == 0) {
        std::string out = std::string("{\"valid\":true,\"normalized\":\"")
                        + normalized + "\"}";
        *result_json = strdup(out.c_str());
        ai_free_string(normalized);
        return 0;
    }
    std::string out = std::string("{\"valid\":false,\"error\":\"")
                    + (error ? error : "unknown") + "\"}";
    *result_json = strdup(out.c_str());
    if (error) ai_free_string(error);
    return 0;   // 校验失败不是 API 错误，结果在 JSON 中表达
}

int ai_symbol_execute(const char* expr_utf8, char** result_json) {
    char* value = nullptr; char* proof = nullptr; char* error = nullptr;
    int rc = sympy_execute(expr_utf8, &value, &proof, &error);
    std::string status = (rc==0) ? "OK" : (rc==1 ? "UNSUPPORTED" : "ERROR");
    std::string out = std::string("{\"status\":\"") + status + "\"";
    if (value) { out += ",\"value\":\""; out += value; out += "\""; }
    if (proof) { out += ",\"proof\":\""; out += proof; out += "\""; }
    if (error) { out += ",\"error\":\""; out += error; out += "\""; }
    out += "}";
    *result_json = strdup(out.c_str());
    if (value) ai_free_string(value);
    if (proof) ai_free_string(proof);
    if (error) ai_free_string(error);
    return 0;
}

} // extern "C"