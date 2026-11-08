extern "C" int ai_consistency_check(const char* text_utf8, char** result_json) {
    // 输入：LLM 生成的开放域文本
    // 输出：{"consistent":bool,"conflicts":[...]}
    // 实现：提取三元组 -> 查询 knowledge_facts FTS5 -> 比对数值/极性
    // 骨架中返回保守结果
    const char* out = "{\"consistent\":true,\"conflicts\":[]}";
    *result_json = strdup(out);
    return 0;
}