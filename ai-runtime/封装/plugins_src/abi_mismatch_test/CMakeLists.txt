add_library(abi_mismatch_test SHARED abi_mismatch.cpp)

target_include_directories(abi_mismatch_test PRIVATE "${AI_RUNTIME_INCLUDE_DIR}")
target_compile_definitions(abi_mismatch_test PRIVATE PLUGIN_BUILD)

if(MSVC)
    # 与 echo_plugin 相同：必须按 UTF-8 解码中文注释，否则导出声明会被吞掉
    target_compile_options(abi_mismatch_test PRIVATE /utf-8)
endif()

set_target_properties(abi_mismatch_test PROPERTIES OUTPUT_NAME abi_mismatch_test)
ai_runtime_output(abi_mismatch_test "${CMAKE_BINARY_DIR}/staging/plugins")
