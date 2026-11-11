# ============================================================================
# logic_engine：确定性路由 / 关键字提取 / 符号裁决 / 一致性校验
#
# 只链接 sympy_engine 的 C ABI；不包含任何 Python 运行时。
# ============================================================================
add_library(logic_engine SHARED
    ai_route.cpp
    ai_keywords.cpp
    ai_symbol.cpp
    ai_verify.cpp
    ai_consistency.cpp
    keyword_tables.cpp)

target_include_directories(logic_engine PRIVATE
    "${AI_RUNTIME_INCLUDE_DIR}"
    "${CMAKE_CURRENT_SOURCE_DIR}")

target_compile_definitions(logic_engine PRIVATE LOGIC_ENGINE_EXPORTS)

if(TARGET sympy_engine)
    target_link_libraries(logic_engine PRIVATE sympy_engine)
elseif(EXISTS "${AI_RUNTIME_ROOT}/封装/prebuilt/sympy_engine.lib")
    target_link_libraries(logic_engine PRIVATE
        "${AI_RUNTIME_ROOT}/封装/prebuilt/sympy_engine.lib")
    add_custom_command(TARGET logic_engine POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${AI_RUNTIME_ROOT}/封装/prebuilt/sympy_engine.dll"
            "$<TARGET_FILE_DIR:logic_engine>/sympy_engine.dll")
else()
    message(FATAL_ERROR "sympy_engine 既没有源码也没有 prebuilt/sympy_engine.lib")
endif()

if(MSVC)
    # 中文字面量必须按 UTF-8 解码
    target_compile_options(logic_engine PRIVATE /utf-8)
endif()

set_target_properties(logic_engine PROPERTIES OUTPUT_NAME logic_engine)
ai_runtime_output(logic_engine "${AI_RUNTIME_STAGING_NATIVE}")
