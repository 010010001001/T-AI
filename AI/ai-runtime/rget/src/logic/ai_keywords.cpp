#include <string>
#include <string_view>
#include <vector>
#include <cstdlib>
#include <cstring>

namespace {

struct Pattern {
    // 时间：17号、2026-11-08、今天、昨天
    // 动作：计算、搜索、评价、打开、保存
    // 实体：由词表提供（夹具模具、家具、软件名...）
};

// 词表由 build/keywords.json 在构建时编译进 .cpp（或运行时从 plugins 目录旁的 kw.bin 读取）
static const std::vector<std::string> g_action_verbs = {
    "计算","搜索","查询","评价","打开","保存","删除","修改","比较","分析"
};
static const std::vector<std::string> g_entities = {
    // 构建时由领域配置注入
    "夹具","模具","家具","软件"
};

static std::string scan_time(std::string_view t) {
    // 简化：匹配 "N号" / "N月" / ISO 日期；实际实现用状态机
    // 返回 "17号" 或空
    return {};
}

static std::string extract_summary(std::string_view text) {
    // 规则：每命中一个 (时间 + 动作 + 实体) 三元组，产出一句摘要
    // 例："17号执行了夹具模具的计算，搜索了家具，评价软件不好用"
    std::string out;
    // ... 三元组组装逻辑，骨架省略
    return out;
}

} // namespace

extern "C" int ai_extract_keywords(const char* text_utf8, char** result_json) {
    std::string_view t(text_utf8);
    std::string summary = extract_summary(t);

    // 关键字列表：命中的动作、实体、时间表达式的去重集合
    std::string keywords = "[]";   // 骨架中省略填充

    std::string out = std::string("{\"keywords\":") + keywords
                    + ",\"summary\":\"" + summary + "\"}";
    *result_json = strdup(out.c_str());
    return 0;
}