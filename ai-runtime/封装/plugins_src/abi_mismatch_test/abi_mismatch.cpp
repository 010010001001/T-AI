// plugins_src/abi_mismatch_test/abi_mismatch.cpp
//
// 故意返回不匹配的 ABI 版本，用于验证 PluginManager 会拒绝而不是崩溃。

#define PLUGIN_BUILD 1   // 确保定义（即使构建系统未传 /DPLUGIN_BUILD）也带 dllexport
#include "plugin_abi.h"

#include <cstdlib>

extern "C" {

int plugin_abi_version(void) { return AI_PLUGIN_ABI_VERSION + 1; }

int plugin_describe(char** description_json)
{
    if (!description_json) return 1;
    *description_json = nullptr;
    return 1;
}

int plugin_init(const char* config_json, void** out_handle)
{
    (void)config_json;
    if (out_handle) *out_handle = nullptr;
    return 1;
}

int plugin_execute(void* handle, const char* input_json, char** output_json)
{
    (void)handle; (void)input_json;
    if (output_json) *output_json = nullptr;
    return 1;
}

void plugin_destroy(void* handle) { (void)handle; }

void plugin_free_string(char* s) { std::free(s); }

} // extern "C"
