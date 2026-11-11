cmake_minimum_required(VERSION 3.20)

project(ai_runtime LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(AI_RUNTIME_ROOT "${CMAKE_CURRENT_SOURCE_DIR}")
set(AI_RUNTIME_INCLUDE_DIR "${AI_RUNTIME_ROOT}/封装/include")
set(AI_RUNTIME_STAGING_NATIVE "${CMAKE_BINARY_DIR}/staging/native")

# 多配置生成器（Visual Studio）默认会在输出目录后追加 Debug/Release 子目录。
# 统一把所有配置的输出钉在同一处，发布脚本才能按固定路径收集。
function(ai_runtime_output target outdir)
    set_target_properties(${target} PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY           "${outdir}"
        RUNTIME_OUTPUT_DIRECTORY_DEBUG     "${outdir}"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE   "${outdir}"
        LIBRARY_OUTPUT_DIRECTORY           "${outdir}"
        LIBRARY_OUTPUT_DIRECTORY_DEBUG     "${outdir}"
        LIBRARY_OUTPUT_DIRECTORY_RELEASE   "${outdir}"
        ARCHIVE_OUTPUT_DIRECTORY           "${outdir}"
        ARCHIVE_OUTPUT_DIRECTORY_DEBUG     "${outdir}"
        ARCHIVE_OUTPUT_DIRECTORY_RELEASE   "${outdir}")
endfunction()

option(AIRUNTIME_BUILD_INFERENCE "构建 llama.cpp 推理引擎 ai_inference" ON)
option(AIRUNTIME_BUILD_PLUGINS "构建示例插件" ON)

# ---------------------------------------------------------------------------
# llama.cpp：作为子目录引入；未 vendored 时只构建逻辑引擎
# ---------------------------------------------------------------------------
set(LLAMA_CPP_DIR "${AI_RUNTIME_ROOT}/third_party/llama.cpp")
if(EXISTS "${LLAMA_CPP_DIR}/CMakeLists.txt")
    add_subdirectory("${LLAMA_CPP_DIR}" EXCLUDE_FROM_ALL)
else()
    message(WARNING "third_party/llama.cpp 不存在：跳过 ai_inference 构建。请参考 README 拉取子模块。")
    set(AIRUNTIME_BUILD_INFERENCE OFF)
endif()

# ---------------------------------------------------------------------------
# sympy_engine：优先源码构建；源码缺失时使用 prebuilt 导入库
# ---------------------------------------------------------------------------
set(SYMPY_DISPATCH "${AI_RUNTIME_ROOT}/封装/generated/sympy_engine.cpp")

if(EXISTS "${SYMPY_DISPATCH}")
    add_library(sympy_engine SHARED "${SYMPY_DISPATCH}")
    target_include_directories(sympy_engine PRIVATE "${AI_RUNTIME_INCLUDE_DIR}")
    target_compile_definitions(sympy_engine PRIVATE SYMPY_ENGINE_EXPORTS)
    set_target_properties(sympy_engine PROPERTIES OUTPUT_NAME sympy_engine)
    ai_runtime_output(sympy_engine "${AI_RUNTIME_STAGING_NATIVE}")
    if(MSVC)
        target_compile_options(sympy_engine PRIVATE /utf-8)
    endif()
else()
    message(STATUS "sympy_engine 源码缺失，使用 prebuilt 导入库")
endif()

# ---------------------------------------------------------------------------
# 逻辑引擎与推理引擎
# ---------------------------------------------------------------------------
add_subdirectory(rget/src/logic)

if(AIRUNTIME_BUILD_INFERENCE)
    add_subdirectory(rget/src/inference)
endif()

# ---------------------------------------------------------------------------
# 示例插件
# ---------------------------------------------------------------------------
if(AIRUNTIME_BUILD_PLUGINS AND EXISTS "${AI_RUNTIME_ROOT}/封装/plugins_src/echo_plugin/echo_plugin.cpp")
    add_subdirectory(封装/plugins_src/echo_plugin)
    add_subdirectory(封装/plugins_src/abi_mismatch_test)
endif()
