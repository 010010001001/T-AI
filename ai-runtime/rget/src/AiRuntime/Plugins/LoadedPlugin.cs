// src/AiRuntime/Plugins/LoadedPlugin.cs
using System;
using System.Runtime.InteropServices;
using System.Threading;

// 与阶段四 PluginManager.cs 保持一致，不加命名空间。

// ---------------------------------------------------------------------------
// C ABI 函数指针的托管委托定义。
// 必须带 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]，
// 否则默认走 StdCall（x86 上会栈不平衡；x64 上调用约定统一但语义仍不准确）。
// ---------------------------------------------------------------------------

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int PluginExecuteFn(
    IntPtr handle,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string inputJson,
    out IntPtr outputJson);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginDestroyFn(IntPtr handle);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginFreeStringFn(IntPtr s);

/// <summary>
/// 持有插件运行时所需的三个函数指针。
/// 这个类存在的唯一理由是：Marshal.GetDelegateForFunctionPointer 返回的委托
/// 是普通托管对象，若没有字段强引用，GC 可能在插件仍被调用时回收其 thunk，
/// 导致原生层跳转到已释放内存。把三个委托放进一个被 LoadedPlugin 持有的实例，
/// 保证它们的生命周期覆盖整个插件使用期。
/// </summary>
internal sealed class PluginDelegates
{
    internal readonly PluginExecuteFn Execute;
    internal readonly PluginDestroyFn Destroy;
    internal readonly PluginFreeStringFn FreeString;

    internal PluginDelegates(
        PluginExecuteFn execute,
        PluginDestroyFn destroy,
        PluginFreeStringFn freeString)
    {
        Execute = execute ?? throw new ArgumentNullException(nameof(execute));
        Destroy = destroy ?? throw new ArgumentNullException(nameof(destroy));
        FreeString = freeString ?? throw new ArgumentNullException(nameof(freeString));
    }
}

/// <summary>
/// 一个已成功加载并初始化的原生插件。
/// 构造由 PluginManager.LoadOne 完成；外部只能通过 Descriptor 与 Execute 使用它。
/// </summary>
/// <remarks>
/// 线程安全：Execute 内部用 SemaphoreSlim 串行化。插件实例句柄（plugin_init 返回的
/// void*）可能承载文件句柄、网络连接等状态，并发 Execute 的行为由插件自身决定；
/// 在契约层面禁止并发是更安全的默认。
///
/// 生命周期：Dispose 必须先 plugin_destroy 再 FreeLibrary，顺序不可颠倒——
/// plugin_destroy 的实现可能引用 DLL 内的代码段或静态数据，FreeLibrary 之后
/// 这些地址不再有效。本类不实现 finalizer：FreeLibrary 在 finalizer 线程调用
/// 可能导致 loader lock 死锁。若调用方忘记 Dispose，进程退出时 OS 会回收 DLL，
/// 但 plugin_destroy 不会被调用，插件内部资源（临时文件、子进程等）可能泄漏。
/// 这是已知限制，PluginManager 应在应用关闭时显式 DisposeAll。
/// </remarks>
public sealed class LoadedPlugin : IDisposable
{
    private readonly IntPtr _module;
    private readonly PluginDelegates _delegates;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IntPtr _handle;       // plugin_init 返回的实例句柄；Dispose 后置零
    private int _disposed;        // 0 = 活跃，1 = 已释放；用 Interlocked 访问

    /// <summary>加载时由 plugin_describe 解析出的元数据。</summary>
    public PluginDescriptor Descriptor { get; }

    /// <summary>便捷访问 Descriptor.Name，供路由匹配与日志使用。</summary>
    public string Name => Descriptor.Name;

    internal LoadedPlugin(
        IntPtr module,
        PluginDescriptor descriptor,
        IntPtr handle,
        PluginDelegates delegates)
    {
        if (module == IntPtr.Zero)
            throw new ArgumentException("module must not be zero", nameof(module));
        if (descriptor is null)
            throw new ArgumentNullException(nameof(descriptor));
        if (handle == IntPtr.Zero)
            throw new ArgumentException("handle must not be zero", nameof(handle));

        _module = module;
        Descriptor = descriptor;
        _handle = handle;
        _delegates = delegates ?? throw new ArgumentNullException(nameof(delegates));
    }

    /// <summary>
    /// 调用 plugin_execute，返回插件输出的 JSON 字符串。
    /// 输入与输出均为 UTF-8 JSON；插件返回非零时抛 PluginExecutionException。
    /// </summary>
    /// <exception cref="ObjectDisposedException">已 Dispose 后调用。</exception>
    /// <exception cref="PluginExecutionException">plugin_execute 返回非零。</exception>
    public string Execute(string inputJson)
    {
        if (inputJson is null) throw new ArgumentNullException(nameof(inputJson));
        ThrowIfDisposed();

        _gate.Wait();
        try
        {
            // 双重检查：等待锁期间 Dispose 可能已完成
            ThrowIfDisposed();

            int rc = _delegates.Execute(_handle, inputJson, out IntPtr output);
            try
            {
                if (rc != 0)
                {
                    string? detail = output != IntPtr.Zero
                        ? Marshal.PtrToStringUTF8(output)
                        : null;
                    throw new PluginExecutionException(Name, rc, detail);
                }
                if (output == IntPtr.Zero)
                    return "{}";   // 契约上返回 0 时 output 应非空；防御性兜底
                return Marshal.PtrToStringUTF8(output) ?? "{}";
            }
            finally
            {
                // 无论 rc 是否为 0、是否抛异常，只要插件分配了 output 就必须释放。
                // 关键：用该插件自己的 FreeString，不能用其他插件的，也不能用
                // ai_inference / logic_engine 的 ai_free_string——每个 DLL 有自己的堆。
                if (output != IntPtr.Zero)
                    _delegates.FreeString(output);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // 等待 in-flight Execute 完成，保证 plugin_destroy 不会与 plugin_execute 并发
        _gate.Wait();
        try
        {
            if (_handle != IntPtr.Zero)
            {
                try { _delegates.Destroy(_handle); }
                catch (Exception)
                {
                    // 插件的 destroy 崩溃不应阻止 FreeLibrary；
                    // 此处吞掉异常是刻意的，日志由 PluginManager 在更高层记录。
                }
                _handle = IntPtr.Zero;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }

        // FreeLibrary 放在 gate 之外：此时已无并发 Execute，且 FreeLibrary 本身
        // 可能触发 DllMain(DLL_PROCESS_DETACH)，不应在持有托管锁时调用。
        if (_module != IntPtr.Zero)
        {
            PluginNative.FreeLibrary(_module);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(LoadedPlugin));
    }

    public override string ToString() => $"{Descriptor.Name} v{Descriptor.Version}";
}

/// <summary>
/// plugin_execute 返回非零时抛出。NativeDetail 是插件通过 output_json 回传的错误描述
/// （如果插件在失败时也分配了 output）；为 null 表示插件未提供细节。
/// </summary>
public sealed class PluginExecutionException : Exception
{
    public string PluginName { get; }
    public int ReturnCode { get; }
    public string? NativeDetail { get; }

    public PluginExecutionException(string pluginName, int returnCode, string? nativeDetail)
        : base(BuildMessage(pluginName, returnCode, nativeDetail))
    {
        PluginName = pluginName;
        ReturnCode = returnCode;
        NativeDetail = nativeDetail;
    }

    private static string BuildMessage(string name, int rc, string? detail) =>
        detail is null
            ? $"plugin '{name}' execute failed rc={rc}"
            : $"plugin '{name}' execute failed rc={rc}: {detail}";
}