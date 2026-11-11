add_library(echo_plugin SHARED echo_plugin.cpp)

target_include_directories(echo_plugin PRIVATE "${AI_RUNTIME_INCLUDE_DIR}")
target_compile_definitions(echo_plugin PRIVATE PLUGIN_BUILD)

if(MSVC)
    # 源文件为 UTF-8 无 BOM；不加 /utf-8 时 MSVC 按系统代码页(936)解码，
    # 中文注释可能吞掉后续行（包括 dllexport 声明），导致插件没有导出符号。
    target_compile_options(echo_plugin PRIVATE /utf-8)
endif()

if(MSVC)
    target_compile_options(echo_plugin PRIVATE /utf-8)
endif()

set_target_properties(echo_plugin PROPERTIES OUTPUT_NAME echo_plugin)
ai_runtime_output(echo_plugin "${CMAKE_BINARY_DIR}/staging/plugins")
