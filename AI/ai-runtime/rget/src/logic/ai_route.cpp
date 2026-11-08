#include <string>
#include <string_view>
#include <cstring>
#include <cstdlib>
#include <vector>

namespace {

enum class Route { SYMBOLIC, TOOL, OPEN };

// 关键词表在构建时生成，运行时只读加载（见 §5）
struct KeywordTable {
    std::vector<std::string> symbolic_ops;   // "+", "-", "*", "/", "=", "解", "计算", "求"
    std::vector<std::string> tool_verbs;     // "搜索", "查询", "打开", "读取", "保存"
    std::vector<std::string> open_markers;   // "感觉", "评价", "觉得", "喜欢", "写一首"
};

static KeywordTable g_kw;

static bool contains_any(std::string_view text, const std::vector<std::string>& table) {
    for (const auto& kw : table)
        if (text.find(kw) != std::string_view::npos) return true;
    return false;
}

static bool has_math_operator(std::string_view text) {
    for (char c : text)
        if (c=='+'||c=='-'||c=='*'||c=='/'||c=='='||c=='^'||c=='('||c==')') return true;
    return false;
}

static Route decide(std::string_view q) {
    // 优先级：TOOL > SYMBOLIC > OPEN
    // 理由：工具调用涉及副作用，误判为 SYMBOLIC 会导致副作用被静默丢弃
    if (contains_any(q, g_kw.tool_verbs))   return Route::TOOL;
    if (has_math_operator(q) || contains_any(q, g_kw.symbolic_ops)) return Route::SYMBOLIC;
    if (contains_any(q, g_kw.open_markers)) return Route::OPEN;
    return Route::OPEN;   // 默认开放域，由 LLM 兜底并经过一致性校验
}

} // namespace

extern "C" int ai_route(const char* question_utf8, char** result_json) {
    std::string_view q(question_utf8);
    Route r = decide(q);
    const char* name = (r==Route::SYMBOLIC) ? "SYMBOLIC"
                     : (r==Route::TOOL)     ? "TOOL" : "OPEN";
    // 置信度为规则命中的确定性结果，非概率值；固定 1.0 表示"规则明确命中"
    std::string out = std::string("{\"route\":\"") + name + "\",\"confidence\":1.0}";
    *result_json = strdup(out.c_str());
    return 0;
}