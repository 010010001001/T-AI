// src/logic/ai_keywords.cpp
//
// 确定性关键字提取与摘要生成。不调用 LLM，不读取数据库：
//   1. 按标点切分子句；
//   2. 识别时间（"17号"/"2026-11-08"/"今天"）、动作动词、领域实体；
//   3. 每个 (时间, 动作, 实体) 三元组产出一句短摘要；
//   4. 同一段文本内去重，供"眨眼"压缩与回退检索使用。
//
// 摘要格式示例：
//   "17号执行了夹具模具的计算，搜索了家具"
// 用词表外实体不会被提取——这是确定性提取的已知限制。

#include "logic_engine.h"
#include "keyword_tables.h"
#include "json_util.h"

#include <cctype>
#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace {

/* UTF-8 分隔符 */
constexpr std::string_view kSeparators[] = {
    "\xEF\xBC\x8C", // ，
    "\xE3\x80\x82", // 。
    "\xEF\xBC\x81", // ！
    "\xEF\xBC\x9F", // ？
    "\xEF\xBC\x9B", // ；
    "\xE3\x80\x81", // 、
    "\n", "\r", ";", ",", ".", "!", "?",
};

/* 负面/正面评价词。用于"评价软件不好用"这类摘要的极性判定。 */
constexpr std::string_view kNegative[] = {
    "不好用", "不好", "不行", "较差", "很差", "失败", "错误", "难用", "失望", "麻烦",
};
constexpr std::string_view kPositive[] = {
    "好用", "不错", "满意", "优秀", "推荐", "方便", "流畅",
};

bool starts_with(std::string_view s, std::size_t pos, std::string_view token)
{
    return pos + token.size() <= s.size() && s.substr(pos, token.size()) == token;
}

std::vector<std::string> split_clauses(std::string_view text)
{
    std::vector<std::string> clauses;
    std::string current;
    std::size_t i = 0;

    while (i < text.size())
    {
        bool cut = false;
        for (std::string_view sep : kSeparators)
        {
            if (starts_with(text, i, sep))
            {
                i += sep.size();
                cut = true;
                break;
            }
        }

        if (cut)
        {
            if (!current.empty()) { clauses.push_back(current); current.clear(); }
            continue;
        }

        current.push_back(text[i]);
        ++i;
    }

    if (!current.empty()) clauses.push_back(current);
    return clauses;
}

bool is_digit(char c)
{
    return c >= '0' && c <= '9';
}

/* 将 UTF-8 字符串截断到不超过 max_bytes，且不切断多字节字符。 */
std::string utf8_prefix(std::string_view s, std::size_t max_bytes)
{
    if (s.size() <= max_bytes) return std::string(s);

    std::size_t cut = max_bytes;
    while (cut > 0 && (static_cast<unsigned char>(s[cut]) & 0xC0) == 0x80) --cut;
    return std::string(s.substr(0, cut));
}

/* 扫描时间表达式：固定时间词优先，其次数字型（17号 / 11月 / 2026-11-08）。 */
std::string scan_time(std::string_view text)
{
    const KeywordTables& t = GetKeywordTables();

    std::size_t best = std::string_view::npos;
    std::string fixed;

    for (const auto& kw : t.time_fixed)
    {
        std::size_t pos = text.find(kw);
        if (pos != std::string_view::npos && pos < best) { best = pos; fixed = kw; }
    }
    if (!fixed.empty()) return fixed;

    // ISO 日期 2026-11-08 / 2026/11/08
    for (std::size_t i = 0; i + 9 < text.size(); ++i)
    {
        if (is_digit(text[i]) && is_digit(text[i + 1]) && is_digit(text[i + 2]) &&
            is_digit(text[i + 3]) && (text[i + 4] == '-' || text[i + 4] == '/'))
        {
            std::size_t j = i + 5;
            while (j < text.size() && j < i + 10 &&
                   (is_digit(text[j]) || text[j] == '-' || text[j] == '/')) ++j;
            if (j - i >= 8) return std::string(text.substr(i, j - i));
        }
    }

    // 17号 / 11月 / 3日 / 2026年
    for (std::size_t i = 0; i < text.size(); ++i)
    {
        if (!is_digit(text[i])) continue;

        std::size_t start = i;
        while (i < text.size() && is_digit(text[i])) ++i;
        if (i >= text.size()) break;

        std::string_view unit = text.substr(i, 3);
        if (unit == "号" || unit == "日" || unit == "月" || unit == "年")
        {
            std::size_t len = i - start + 3;
            return std::string(text.substr(start, len));
        }
    }

    return std::string();
}

bool contains_negative(std::string_view clause)
{
    for (auto w : kNegative)
        if (clause.find(w) != std::string_view::npos) return true;
    return false;
}

bool contains_positive(std::string_view clause)
{
    for (auto w : kPositive)
        if (clause.find(w) != std::string_view::npos) return true;
    return false;
}

/* 收集子句中出现的全部实体，按词表顺序拼接（"夹具"+"模具" -> "夹具模具"）。 */
std::string collect_entities(std::string_view clause, const KeywordTables& t)
{
    std::string out;
    for (const auto& e : t.entities)
        if (clause.find(e) != std::string_view::npos) out += e;
    return out;
}

std::string first_entity(std::string_view clause, const KeywordTables& t)
{
    std::string best;
    std::size_t bestPos = std::string_view::npos;

    for (const auto& e : t.entities)
    {
        std::size_t pos = clause.find(e);
        if (pos != std::string_view::npos && pos < bestPos) { bestPos = pos; best = e; }
    }
    return best;
}

/* 动作 -> 摘要动词短语 */
std::string action_statement(std::string_view action, std::string_view entity,
                             bool negative, bool positive)
{
    if (entity.empty()) return std::string();

    if (action == "计算") return "执行了" + std::string(entity) + "的计算";
    if (action == "搜索" || action == "查询" || action == "查找" || action == "检索")
        return "搜索了" + std::string(entity);

    if (action == "评价")
    {
        std::string s = "评价" + std::string(entity);
        if (negative) s += "不好用";
        else if (positive) s += "好用";
        return s;
    }

    if (action == "比较" || action == "分析")
        return std::string(action) + "了" + std::string(entity);

    return std::string(action) + "了" + std::string(entity);
}

} // namespace

extern "C" int ai_extract_keywords(const char* text_utf8, char** result_json)
{
    if (!text_utf8 || !result_json) return 1;

    const KeywordTables& t = GetKeywordTables();
    std::string_view text(text_utf8);

    std::string time = scan_time(text);
    std::vector<std::string> statements;
    std::vector<std::string> keywords;
    std::vector<std::string> seen;

    auto add_keyword = [&](const std::string& kw)
    {
        if (kw.empty()) return;
        for (const auto& s : seen) if (s == kw) return;
        seen.push_back(kw);
        keywords.push_back(kw);
    };

    if (!time.empty()) add_keyword(time);

    for (const std::string& clause : split_clauses(text))
    {
        if (clause.empty()) continue;

        std::string entity = collect_entities(clause, t);
        std::string action;
        for (const auto& verb : t.action_verbs)
        {
            if (clause.find(verb) != std::string::npos) { action = verb; break; }
        }

        if (!action.empty()) add_keyword(action);
        for (const auto& e : t.entities)
            if (clause.find(e) != std::string::npos) add_keyword(e);

        if (action.empty() || entity.empty()) continue;

        bool negative = contains_negative(clause);
        bool positive = !negative && contains_positive(clause);

        std::string statement;
        if (action == "搜索" || action == "查询" || action == "查找" || action == "检索")
        {
            // 搜索类只取第一个实体，避免 "家具行业网页" 这种堆叠
            statement = action_statement(action, first_entity(clause, t), negative, positive);
        }
        else
        {
            statement = action_statement(action, entity, negative, positive);
        }

        if (statement.empty()) continue;

        bool duplicate = false;
        for (const auto& s : statements) if (s == statement) { duplicate = true; break; }
        if (!duplicate) statements.push_back(statement);
    }

    std::string summary;
    for (std::size_t i = 0; i < statements.size(); ++i)
    {
        if (i == 0 && !time.empty() && statements[i].rfind(time, 0) != 0)
            summary += time;
        if (i > 0) summary += "，";
        summary += statements[i];
    }

    // 没有任何三元组命中时，退化为原文首句的截断，保证摘要非空
    if (summary.empty())
    {
        std::string_view first = text;
        for (std::string_view sep : kSeparators)
        {
            std::size_t pos = first.find(sep);
            if (pos != std::string_view::npos) first = first.substr(0, pos);
        }
        summary = utf8_prefix(first, 60);
    }

    std::string keywordsJson = "[";
    for (std::size_t i = 0; i < keywords.size(); ++i)
    {
        if (i > 0) keywordsJson += ",";
        keywordsJson += ai_json::quote(keywords[i]);
    }
    keywordsJson += "]";

    std::string out = std::string("{\"keywords\":") + keywordsJson +
                      ",\"summary\":" + ai_json::quote(summary) + "}";

    *result_json = ai_json::strdup_alloc(out);
    return *result_json ? 0 : 1;
}
