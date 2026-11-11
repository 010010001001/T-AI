#pragma once
#include <stdint.h>

#ifdef _WIN32
  #ifdef PLUGIN_BUILD
    #define PLUGIN_API __declspec(dllexport)
  #else
    #define PLUGIN_API
  #endif
#else
  #define PLUGIN_API __attribute__((visibility("default")))
#endif

// ============================================================================
// plugin_abi.h：C ABI 插件契约（v1）
//
// 所有函数均为 C 调用约定（Cdecl），由插件 DLL 导出。
// 描述字符串与 execute 输出均由插件用自己的分配器分配，必须由插件的
// plugin_free_string 释放；跨 DLL 释放字符串会损坏堆。
// ============================================================================
#ifdef __cplusplus
extern "C" {
#endif

#define AI_PLUGIN_ABI_VERSION 1

// 每个插件必须导出此符号；加载器首先查找它。返回 ABI 版本号，不匹配则拒绝加载。
PLUGIN_API int plugin_abi_version(void);

// 每个插件必须导出：返回 JSON 描述自身能力
// {"name":"...","version":"...","functions":["search","read_file"]}
// *description_json 由插件分配，调用方用 plugin_free_string 释放。
PLUGIN_API int plugin_describe(char** description_json);

// 初始化：config_json 来自与 DLL 同名的 .json 文件（不存在时传 "{}"）。
// 成功时返回 0 并写出实例句柄。
PLUGIN_API int plugin_init(const char* config_json, void** out_handle);

// 执行：输入/输出均为 UTF-8 JSON 字符串。返回 0 表示成功。
// *output_json 由插件分配，调用方用 plugin_free_string 释放。
PLUGIN_API int plugin_execute(void* handle, const char* input_json, char** output_json);

// 销毁实例。之后加载器才会 FreeLibrary，顺序不可颠倒。
PLUGIN_API void plugin_destroy(void* handle);

// 释放插件分配的字符串。
PLUGIN_API void plugin_free_string(char* s);

#ifdef __cplusplus
}
#endif
