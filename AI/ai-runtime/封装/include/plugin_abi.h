#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

#define AI_PLUGIN_ABI_VERSION 1

// 每个插件必须导出此符号；加载器首先查找它
// 返回 ABI 版本号；不匹配则拒绝加载
int plugin_abi_version(void);

// 每个插件必须导出：返回 JSON 描述自身能力
// {"name":"...","version":"...","functions":["search","read_file"]}
int plugin_describe(char** description_json);

// 已锁定的三个函数
int plugin_init(const char* config_json, void** out_handle);
int plugin_execute(void* handle, const char* input_json, char** output_json);
void plugin_destroy(void* handle);
void plugin_free_string(char* s);

#ifdef __cplusplus
}
#endif