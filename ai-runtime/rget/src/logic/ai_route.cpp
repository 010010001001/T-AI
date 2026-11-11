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

/* 数字/变量-运算符-数字/变量 才算数学信号；同时识别不等式比较符。 */
static bool has_math_operator(std::string_view text)
{
    static const char kOps[] = "+-*/^=<>";

    for (size_t i = 0; i + 1 < text.size(); ++i)
    {
        char c = text[i];
        if (!std::string_view(kOps).find(c)) continue;

        // 运算符右侧跳过空白后必须是数字、变量 x、括号
        size_t j = i + 1;
        while (j < text.size() && (text[j] == ' ' || text[j] == '\t')) ++j;
        bool right = j < text.size() && (std::isdigit(static_cast<unsigned char>(text[j])) ||
                                         text[j] == '(' || text[j] == '[' ||
                                         text[j] == 'x' || text[j] == 'X' ||
                                         (text[j] == '-' && j + 1 < text.size()));
        if (!right) continue;

        // 左侧跳过空白后必须是数字、变量 x、')' 或 ']'
        size_t k = i;
        while (k > 0 && (text[k - 1] == ' ' || text[k - 1] == '\t')) --k;
        bool left = k > 0 && (std::isdigit(static_cast<unsigned char>(text[k - 1])) ||
                              text[k - 1] == 'x' || text[k - 1] == 'X' ||
                              text[k - 1] == ')' || text[k - 1] == ']');
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
