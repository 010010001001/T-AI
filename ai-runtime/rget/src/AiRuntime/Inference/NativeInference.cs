// src/AiRuntime/Inference/NativeInference.cs
using System.Runtime.InteropServices;

/// <summary>
/// ai_inference.dll 的 P/Invoke 声明。只声明符号，不做 JSON 解析与内存管理。
/// </summary>
/// <remarks>
/// 错误码约定：
///   0  成功
///   非 0 失败；*error_json 可能由原生层分配，非空时必须用 ai_free_string 释放。
///
/// 内存所有权：ai_inference.dll 与 logic_engine.dll / 插件各自使用独立分配器，
/// 不能用 LogicNative.FreeString 或插件自己的 free_string 释放本 DLL 的字符串。
/// </remarks>
internal static class Native
{
    private const string Dll = "ai_inference";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TokenCallback(IntPtr tokenUtf8, IntPtr userData);

    /// <summary>加载 GGML 后端（CUDA / CPU）。只初始化后端，不加载模型。</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_backend_init(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string backendsDir);

    /// <summary>加载 GGUF 模型并创建推理上下文。返回 0 表示成功。</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_model_load(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ggufPath,
        out IntPtr errorJson);

    /// <summary>模型是否已加载（1 = 已加载）。</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_model_loaded();

    /// <summary>
    /// 流式生成。token 以 UTF-8 片段形式通过 cb 回调同步送回；
    /// 回调在原生生成线程上执行，异常不得穿过 P/Invoke 边界。
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_generate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string paramsJson,
        TokenCallback cb, IntPtr userData, out IntPtr errorJson);

    /// <summary>精确 token 计数（使用模型词表）。返回 -1 表示失败。</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_count_tokens(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ai_free_string(IntPtr s);
}
