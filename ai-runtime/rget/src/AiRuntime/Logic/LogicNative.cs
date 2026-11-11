// src/AiRuntime/Logic/LogicNative.cs
using System;
using System.Runtime.InteropServices;

/// <summary>
/// logic_engine.dll 的 P/Invoke 声明。
/// 只声明符号，不做 JSON 解析、不做内存所有权管理——那些是 LogicEngine 的职责。
/// </summary>
/// <remarks>
/// 错误码约定（与 ai_inference.dll 不同）：
///   - 返回 0 表示“函数执行完毕，结果在 result_json 中”，
///     即使 result_json 内的 status 是 "ERROR" 或 "UNSUPPORTED"。
///   - 返回非 0 表示“函数本身调用失败”（如参数为空、内部异常），
///     此时 result_json 可能未写入，调用方不应解析它。
///
/// 内存所有权：
///   所有 out IntPtr resultJson 由原生层用 malloc/strdup 分配，
///   调用方必须用 LogicNative.FreeString 释放，不能用 Marshal.FreeHGlobal，
///   也不能用推理层的 Native.ai_free_string。
///
/// 线程安全：
///   原生层假设无全局可变状态（路由与关键字提取只读词表，
///   sympy_execute 无跨调用缓存）。因此本类不加 SemaphoreSlim。
///   若将来原生层引入缓存，必须在 LogicEngine 层加锁，而不是在此处。
/// </remarks>
internal static class LogicNative
{
    private const string Dll = "logic_engine";   // 由 Program.cs 的 SetDllImportResolver 解析到 native/logic_engine.dll

    /// <summary>
    /// 意图路由。返回 {"route":"SYMBOLIC|TOOL|OPEN","confidence":1.0}
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_route(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string question,
        out IntPtr resultJson);

    /// <summary>
    /// 符号表达式语法校验。返回 {"valid":true|false,"normalized":"..."} 或 {"valid":false,"error":"..."}
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_symbol_validate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string expr,
        out IntPtr resultJson);

    /// <summary>
    /// 符号运算。返回 {"status":"OK|UNSUPPORTED|ERROR","value":"...","proof":"..."}
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_symbol_execute(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string expr,
        out IntPtr resultJson);

    /// <summary>
    /// 确定性关键字提取（不调用 LLM）。返回 {"keywords":[...],"summary":"..."}
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_extract_keywords(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        out IntPtr resultJson);

    /// <summary>
    /// 开放域一致性校验。返回 {"consistent":true|false,"conflicts":[...]}
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_consistency_check(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        out IntPtr resultJson);

    /// <summary>
    /// LLM 候选表达式的符号引擎裁决。返回 {"accepted":true|false,"value":"...","reason":"..."}
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_verify_candidate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string candidate,
        out IntPtr resultJson);

    /// <summary>
    /// 释放逻辑引擎分配的字符串。
    /// 必须用 EntryPoint 显式绑定，并重命名 C# 方法：
    /// logic_engine.dll 与 ai_inference.dll 都导出 ai_free_string，
    /// 若两个 DllImport 都用默认方法名，编译器不会报错，
    /// 但运行时可能把逻辑层的指针交给推理层的释放函数，导致堆损坏。
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ai_free_string")]
    internal static extern void FreeString(IntPtr s);
}