/* include/keyword_tables.h
 *
 * 逻辑引擎使用的确定性词表。由 keyword_tables.cpp 在首次访问时构建一次，
 * 之后只读；不依赖 LLM，也不在运行时解析 JSON。
 *
 * 修改词表后必须重新编译 logic_engine.dll。词表文件必须保存为 UTF-8，
 * MSVC 编译时需加 /utf-8，否则中文字面量会按系统代码页解码。
 */
#ifndef AI_KEYWORD_TABLES_H
#define AI_KEYWORD_TABLES_H

#include <string>
#include <vector>

struct KeywordTables
{
    // 语言层面的"要求计算"信号（"计算""求""化简"……）
    std::vector<std::string> symbolic_ops;
    // 工具调用动词，路由优先级最高（有副作用，不能误判给符号引擎）
    std::vector<std::string> tool_verbs;
    // 开放域标记（主观评价、创意、模糊语义）
    std::vector<std::string> open_markers;
    // 摘要动作词，比 tool_verbs 更宽
    std::vector<std::string> action_verbs;
    // 领域实体词（部署时按目标领域替换）
    std::vector<std::string> entities;
    // 固定时间词
    std::vector<std::string> time_fixed;
};

const KeywordTables& GetKeywordTables();

#endif /* AI_KEYWORD_TABLES_H */
