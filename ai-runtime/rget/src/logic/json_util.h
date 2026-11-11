/* src/logic/json_util.h
 *
 * logic_engine 内部最小的 JSON 字符串转义与分配工具。
 * 原生层所有返回给 C# 的字符串都必须经由 ai_json::strdup_alloc 分配，
 * 由 logic_engine.dll 导出的 ai_free_string 释放。
 */
#ifndef AI_LOGIC_JSON_UTIL_H
#define AI_LOGIC_JSON_UTIL_H

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>

namespace ai_json
{

inline std::string escape(std::string_view in)
{
    std::string out;
    out.reserve(in.size() + 8);

    for (unsigned char c : in)
    {
        switch (c)
        {
        case '"':  out += "\\\"";  break;
        case '\\': out += "\\\\";  break;
        case '\b': out += "\\b";   break;
        case '\f': out += "\\f";   break;
        case '\n': out += "\\n";   break;
        case '\r': out += "\\r";   break;
        case '\t': out += "\\t";   break;
        default:
            if (c < 0x20)
            {
                char buf[8];
                std::snprintf(buf, sizeof(buf), "\\u%04X", static_cast<unsigned>(c));
                out += buf;
            }
            else
            {
                out.push_back(static_cast<char>(c));
            }
        }
    }
    return out;
}

inline std::string quote(std::string_view s)
{
    return "\"" + escape(s) + "\"";
}

/* 分配一份调用方可用 ai_free_string / free 释放的副本。 */
inline char* strdup_alloc(std::string_view s)
{
    char* p = static_cast<char*>(std::malloc(s.size() + 1));
    if (!p) return nullptr;
    std::memcpy(p, s.data(), s.size());
    p[s.size()] = '\0';
    return p;
}

} // namespace ai_json

#endif /* AI_LOGIC_JSON_UTIL_H */
