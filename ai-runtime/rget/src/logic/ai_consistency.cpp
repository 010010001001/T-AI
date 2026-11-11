// src/logic/ai_consistency.cpp
//
// 开放域文本的形式化一致性校验。只做"文本内部可被规则证明"的矛盾检测：
//   - 同一实体/谓词同时出现肯定与否定（"支持" vs "不支持"）；
//   - 显式否定数量后又给出不同数量（"不是 3 而是 5"）暂不处理。
// 与知识库已确认事实的交叉校验由 C# 层查询 knowledge_facts 完成——
// 原生层不持有数据库连接。
//
// 检测不到矛盾时返回 consistent=true；不做"我是否错了"的自我评估，
// 避免回退机制退化为无限循环。

#include "logic_engine.h"
#include "json_util.h"

#include <string>
#include <string_view>
#include <vector>

namespace {

constexpr std::string_view kPredicates[] = {
    "支持", "有效", "可行", "正确", "成功", "包含", "需要", "推荐", "允许", "存在",
};

bool starts_with(std::string_view s, std::size_t pos, std::string_view token)
{
    return pos + token.size() <= s.size() && s.substr(pos, token.size()) == token;
}

std::vector<std::string> split_clauses(std::string_view text)
{
    static constexpr std::string_view kSep[] = {
        "\xEF\xBC\x8C", "\xE3\x80\x82", "\xEF\xBC\x81", "\xEF\xBC\x9F",
        "\xEF\xBC\x9B", "\n", "\r", ";", ".", "!", "?",
    };

    std::vector<std::string> out;
    std::string current;
    std::size_t i = 0;

    while (i < text.size())
    {
        bool cut = false;
        for (auto sep : kSep)
        {
            if (starts_with(text, i, sep)) { i += sep.size(); cut = true; break; }
        }
        if (cut)
        {
            if (!current.empty()) { out.push_back(current); current.clear(); }
            continue;
        }
        current.push_back(text[i]);
        ++i;
    }
    if (!current.empty()) out.push_back(current);
    return out;
}

bool is_negated(std::string_view clause, std::string_view predicate)
{
    std::size_t pos = clause.find(predicate);
    if (pos == std::string_view::npos) return false;

    // 谓词前一个字是"不/没/未/无"时视为否定形式
    static constexpr std::string_view kNeg[] = {
        "\xE4\xB8\x8D", // 不
        "\xE6\xB2\xA1", // 没
        "\xE6\x9C\xAA", // 未
        "\xE6\x97\xA0", // 无
    };
    for (auto neg : kNeg)
    {
        if (pos >= neg.size() && clause.substr(pos - neg.size(), neg.size()) == neg)
            return true;
    }
    return false;
}

} // namespace

extern "C" int ai_consistency_check(const char* text_utf8, char** result_json)
{
    if (!text_utf8 || !result_json) return 1;

    std::string_view text(text_utf8);
    auto clauses = split_clauses(text);

    std::vector<std::string> conflicts;

    for (std::size_t i = 0; i < clauses.size(); ++i)
    {
        for (std::size_t j = i + 1; j < clauses.size(); ++j)
        {
            for (auto predicate : kPredicates)
            {
                bool inI = clauses[i].find(predicate) != std::string::npos;
                bool inJ = clauses[j].find(predicate) != std::string::npos;
                if (!inI || !inJ) continue;

                bool negI = is_negated(clauses[i], predicate);
                bool negJ = is_negated(clauses[j], predicate);
                if (negI == negJ) continue;

                conflicts.push_back("「" + clauses[i] + "」与「" + clauses[j] + "」对同一谓词的极性相反");
                break;
            }
        }
    }

    std::string out = "{\"consistent\":";
    out += conflicts.empty() ? "true" : "false";
    out += ",\"conflicts\":[";
    for (std::size_t i = 0; i < conflicts.size(); ++i)
    {
        if (i > 0) out += ",";
        out += ai_json::quote(conflicts[i]);
    }
    out += "]}";

    *result_json = ai_json::strdup_alloc(out);
    return *result_json ? 0 : 1;
}
