# ============================================================================
# ai_inference：llama.cpp 推理引擎（C++），对 C# 只暴露整批 generate 接口。
# ============================================================================
add_library(ai_inference SHARED ai_inference.cpp)

target_include_directories(ai_inference PRIVATE
    "${AI_RUNTIME_INCLUDE_DIR}"
    "${LLAMA_CPP_DIR}/include"
    "${LLAMA_CPP_DIR}/ggml/include")

target_compile_definitions(ai_inference PRIVATE AI_INFERENCE_EXPORTS)
target_link_libraries(ai_inference PRIVATE llama ggml)

if(MSVC)
    target_compile_options(ai_inference PRIVATE /utf-8)
endif()

set_target_properties(ai_inference PROPERTIES OUTPUT_NAME ai_inference)
ai_runtime_output(ai_inference "${AI_RUNTIME_STAGING_NATIVE}")
