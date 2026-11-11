# ============================================================================
# FindSympyEngine.cmake
#
# 定位预编译的 SymPy 符号引擎（sympy_engine.dll + sympy_engine.lib），
# 创建 IMPORTED 目标 SympyEngine::SympyEngine。
#
# 用法（在顶层 CMakeLists.txt 中）：
#   list(APPEND CMAKE_MODULE_PATH "${CMAKE_SOURCE_DIR}/cmake")
#   find_package(SympyEngine REQUIRED)
#
# 提供的变量：
#   SympyEngine_FOUND          - 是否找到
#   SympyEngine_INCLUDE_DIR    - 头文件目录
#   SympyEngine_DLL            - 运行时 DLL 路径
#   SympyEngine_IMPLIB         - 导入库 .lib 路径
#
# 提供的目标：
#   SympyEngine::SympyEngine   - 链接此目标即可获得 include 路径与导入库
# ============================================================================

# 守卫：避免重复执行
if(TARGET SympyEngine::SympyEngine)
    return()
endif()

# ---------------------------------------------------------------------------
# 1. 定位头文件
# ---------------------------------------------------------------------------
# 优先在仓库的 include/ 下查找；也允许通过 CMAKE_PREFIX_PATH 或
# SympyEngine_ROOT 从外部安装位置查找。
find_path(SympyEngine_INCLUDE_DIR
    NAMES sympy_engine.h
    HINTS
        "${CMAKE_CURRENT_LIST_DIR}/../include"      # 仓库根/include
        "${SympyEngine_ROOT}/include"
    PATHS
        "${CMAKE_SOURCE_DIR}/include"
    DOC "SympyEngine include directory"
)

# ---------------------------------------------------------------------------
# 2. 定位导入库（.lib）
# ---------------------------------------------------------------------------
# Windows 上 find_library 默认查找 .lib，这正是链接阶段需要的文件。
# 使用 NO_DEFAULT_PATH 限定在 prebuilt/，避免误命中系统或其他位置的同名库。
find_library(SympyEngine_IMPLIB
    NAMES sympy_engine
    HINTS
        "${CMAKE_CURRENT_LIST_DIR}/../prebuilt"     # 仓库根/prebuilt
        "${SympyEngine_ROOT}/lib"
        "${SympyEngine_ROOT}/prebuilt"
    PATHS
        "${CMAKE_SOURCE_DIR}/prebuilt"
    NO_DEFAULT_PATH
    DOC "SympyEngine import library (.lib)"
)

# ---------------------------------------------------------------------------
# 3. 定位运行时 DLL
# ---------------------------------------------------------------------------
# find_library 在 CMake 3.17 之后默认不再查找 .dll，
# 因此必须用 find_file 显式查找。
# IMPORTED_LOCATION 应指向 .dll，IMPORTED_IMPLIB 指向 .lib；
# 二者分开设置是 Windows 导入目标的正确做法。
find_file(SympyEngine_DLL
    NAMES sympy_engine.dll
    HINTS
        "${CMAKE_CURRENT_LIST_DIR}/../prebuilt"
        "${SympyEngine_ROOT}/bin"
        "${SympyEngine_ROOT}/prebuilt"
    PATHS
        "${CMAKE_SOURCE_DIR}/prebuilt"
    NO_DEFAULT_PATH
    DOC "SympyEngine runtime DLL"
)

# ---------------------------------------------------------------------------
# 4. 检查结果
# ---------------------------------------------------------------------------
include(FindPackageHandleStandardArgs)
find_package_handle_standard_args(SympyEngine
    REQUIRED_VARS
        SympyEngine_INCLUDE_DIR
        SympyEngine_IMPLIB
        SympyEngine_DLL
    REASON_FAILURE_MESSAGE
        "sympy_engine 的预编译产物不完整。请确认 prebuilt/ 下同时存在 sympy_engine.dll 与 sympy_engine.lib，且 include/sympy_engine.h 可用。"
)

# 找不到时直接返回，不创建半成品目标
if(NOT SympyEngine_FOUND)
    return()
endif()

# ---------------------------------------------------------------------------
# 5. 创建 IMPORTED 目标
# ---------------------------------------------------------------------------
# 使用 SHARED IMPORTED 而非 UNKNOWN IMPORTED：
#   - SHARED 让 CMake 知道这是动态库，在 Windows 上会同时使用
#     IMPORTED_IMPLIB（链接）与 IMPORTED_LOCATION（运行时）。
#   - UNKNOWN 虽然也能工作，但会丢失“这是动态库”的语义，
#     且无法被 $<TARGET_RUNTIME_DLLS> 等生成器表达式正确识别。
add_library(SympyEngine::SympyEngine SHARED IMPORTED)

set_target_properties(SympyEngine::SympyEngine PROPERTIES
    # 链接阶段使用 .lib
    IMPORTED_IMPLIB                  "${SympyEngine_IMPLIB}"
    # 运行时使用 .dll
    IMPORTED_LOCATION                "${SympyEngine_DLL}"
    # 头文件目录传递给消费者
    INTERFACE_INCLUDE_DIRECTORIES    "${SympyEngine_INCLUDE_DIR}"
    # 标记为全局可见，允许在 add_subdirectory 的任意子目录中链接
    IMPORTED_GLOBAL                  TRUE
)

# ---------------------------------------------------------------------------
# 6. 标记缓存变量为高级，避免淹没 CMake GUI
# ---------------------------------------------------------------------------
mark_as_advanced(
    SympyEngine_INCLUDE_DIR
    SympyEngine_IMPLIB
    SympyEngine_DLL
)