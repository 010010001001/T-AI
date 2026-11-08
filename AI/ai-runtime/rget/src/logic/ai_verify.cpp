extern "C" {

int ai_verify_candidate(const char* candidate_utf8, char** result_json) {
    // 步骤 1：语法校验
    char* norm = nullptr; char* verr = nullptr;
    int vrc = sympy_validate(candidate_utf8, &norm, &verr);

    if (vrc != 0) {
        std::string out = std::string("{\"accepted\":false,\"reason\":\"syntax_invalid\",")
                        + "\"detail\":\"" + (verr ? verr : "") + "\"}";
        *result_json = strdup(out.c_str());
        if (verr) ai_free_string(verr);
        return 0;
    }

    // 步骤 2：符号运算（以符号引擎结果为准，不使用 LLM 的数值）
    char* value = nullptr; char* proof = nullptr; char* eerr = nullptr;
    int erc = sympy_execute(norm, &value, &proof, &eerr);
    ai_free_string(norm);

    if (erc == 0) {
        std::string out = std::string("{\"accepted\":true,\"value\":\"")
                        + (value ? value : "") + "\",\"proof\":\""
                        + (proof ? proof : "") + "\"}";
        *result_json = strdup(out.c_str());
    } else {
        std::string out = std::string("{\"accepted\":false,\"reason\":\"")
                        + (erc == 1 ? "unsupported" : "execution_error")
                        + "\",\"detail\":\"" + (eerr ? eerr : "") + "\"}";
        *result_json = strdup(out.c_str());
    }
    if (value) ai_free_string(value);
    if (proof) ai_free_string(proof);
    if (eerr)  ai_free_string(eerr);
    return 0;
}

} // extern "C"