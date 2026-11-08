cmake_minimum_required(VERSION 3.20)
project(ai_runtime LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)

# llama.cpp 作为子目录引入，或通过 find_package 使用已安装版本
add_subdirectory(third_party/llama.cpp EXCLUDE_FROM_ALL)

add_library(ai_inference SHARED src/inference/ai_inference.cpp)
target_link_libraries(ai_inference PRIVATE llama ggml)
target_include_directories(ai_inference PRIVATE third_party/llama.cpp/include)

# 将 llama/ggml 的 DLL 一并输出到 staging/native
set_target_properties(ai_inference PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/staging/native")