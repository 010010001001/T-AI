// src/logic/ai_route.cpp
#include "logic_engine.h"
#include "keyword_tables.h"
#include "json_util.h"

#include <cctype>
#include <string>
#include <string_view>
#include <vector>

namespace {

enum class Route { SYMBOLIC, TOOL, OPEN };

static bool contains_any(std::string_view text, const std::vector<std::string>& table)
{
    for (const auto& kw : table)
        if (!kw.empty() && text.find(kw) != std::string_view::npos) return true;
    return false;
}

/* 数字-运算符-数字 才算数学信号；避免把日期 "2026-11-08" 之外的 '-'、
 * 文件名中的 '.' 等误判为运算。 */
static bool has_math_operator(std::string_view text)
{
    static const char kOps[] = "+-*/^=";

    for (size_t i = 1; i + 1 < text.size(); ++i)
    {
        char c = text[i];
        if (!std::string_view(kOps).find(c)) continue;

        // 运算符右侧跳过空白后必须是数字或 '('
        size_t j = i + 1;
        while (j < text.size() && (text[j] == ' ' || text[j] == '\t')) ++j;
        bool right = j < text.size() && (std::isdigit(static_cast<unsigned char>(text[j])) ||
                                         text[j] == '(' || text[j] == '-');
        if (!right) continue;

        // 左侧跳过空白后必须是数字、')' 或 'x'
        size_t k = i;
        while (k > 0 && (text[k - 1] == ' ' || text[k - 1] == '\t')) --k;
        bool left = k > 0 && (std::isdigit(static_cast<unsigned char>(text[k - 1])) ||
                              text[k - 1] == ')' || text[k - 1] == 'x');
        if (left) return true;
    }
    return false;
}

static Route decide(std::string_view q)
{
    const KeywordTables& t = GetKeywordTables();

    // 优先级：TOOL > SYMBOLIC > OPEN。
    // 工具调用涉及副作用，误判为 SYMBOLIC 会导致副作用被静默丢弃。
    if (contains_any(q, t.tool_verbs)) return Route::TOOL;

    if (contains_any(q, t.symbolic_ops)) return Route::SYMBOLIC;
    if (has_math_operator(q)) return Route::SYMBOLIC;

    if (contains_any(q, t.open_markers)) return Route::OPEN;

    // 默认开放域：由 LLM 兜底并经过一致性校验
    return Route::OPEN;
}

} // namespace

extern "C" int ai_route(const char* question_utf8, char** result_json)
{
    if (!question_utf8 || !result_json) return 1;

    std::string_view q(question_utf8);
    Route r = decide(q);

    const char* name = (r == Route::SYMBOLIC) ? "SYMBOLIC"
                     : (r == Route::TOOL)     ? "TOOL"
                                              : "OPEN";

    // 置信度为规则命中的确定性结果，非概率值；1.0 表示规则明确命中。
    std::string out = std::string("{\"route\":\"") + name + "\",\"confidence\":1.0}";

    *result_json = ai_json::strdup_alloc(out);
    return *result_json ? 0 : 1;
}
