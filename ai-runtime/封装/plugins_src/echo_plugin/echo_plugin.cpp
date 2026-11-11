// plugins_src/echo_plugin/echo_plugin.cpp
//
// 最小可用插件：验证 C ABI 的完整生命周期（版本、描述、初始化、执行、销毁）。
// 真实插件（搜索、文件、API）应复用同一契约，并在 plugin_describe 中声明能力。

#define PLUGIN_BUILD 1   // 确保定义（即使构建系统未传 /DPLUGIN_BUILD）也带 dllexport
#include "plugin_abi.h"

#include <cstdlib>
#include <cstring>
#include <new>
#include <string>

namespace {

struct EchoHandle
{
    std::string name;
};

char* dup_string(const char* s)
{
    if (!s) return nullptr;
    size_t n = std::strlen(s) + 1;
    char* p = static_cast<char*>(std::malloc(n));
    if (p) std::memcpy(p, s, n);
    return p;
}

} // namespace

extern "C" {

int plugin_abi_version(void) { return AI_PLUGIN_ABI_VERSION; }

int plugin_describe(char** description_json)
{
    if (!description_json) return 1;
    *description_json = dup_string(
        R"({"name":"echo","version":"1.0.0","functions":["echo","search"]})");
    return *description_json ? 0 : 2;
}

int plugin_init(const char* config_json, void** out_handle)
{
    if (!out_handle) return 1;
    (void)config_json;

    EchoHandle* handle = new (std::nothrow) EchoHandle();
    if (!handle) return 2;

    handle->name = "echo";
    *out_handle = handle;
    return 0;
}

int plugin_execute(void* handle, const char* input_json, char** output_json)
{
    if (!handle || !input_json || !output_json) return 1;

    // 输入必须是一个 JSON 值；这里直接回显，便于端到端验证参数传递
    std::string out = "{\"echoed\":";
    out += input_json;
    out += ",\"plugin\":\"echo\"}";

    *output_json = dup_string(out.c_str());
    if (!*output_json) return 2;
    return 0;
}

void plugin_destroy(void* handle)
{
    delete static_cast<EchoHandle*>(handle);
}

void plugin_free_string(char* s) { std::free(s); }

} // extern "C"
