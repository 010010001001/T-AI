using System.Runtime.InteropServices;
using System.Text.Json;

internal static class PluginNative
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool FreeLibrary(IntPtr hModule);
}

public sealed record PluginDescriptor(string Name, string Version, string[] Functions);

public sealed class LoadedPlugin : IDisposable
{
    public PluginDescriptor Descriptor { get; }
    private readonly IntPtr _module;
    private readonly IntPtr _handle;
    private readonly PluginNativeDelegates _delegates;

    // 函数指针委托定义，与 C ABI 严格对应
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExecuteFn(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string input, out IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyFn(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeStringFn(IntPtr s);

    public string Execute(string inputJson)
    {
        int rc = _delegates.Execute(_handle, inputJson, out IntPtr outPtr);
        if (rc != 0)
            throw new InvalidOperationException($"plugin execute failed: {rc}");
        try { return Marshal.PtrToStringUTF8(outPtr) ?? "{}"; }
        finally { _delegates.FreeString(outPtr); }
    }

    public void Dispose()
    {
        _delegates.Destroy(_handle);
        PluginNative.FreeLibrary(_module);
    }
}

public sealed class PluginManager
{
    private readonly string _pluginDir;
    private readonly List<LoadedPlugin> _loaded = new();

    public IReadOnlyList<LoadedPlugin> LoadAll()
    {
        if (!Directory.Exists(_pluginDir)) return _loaded;

        foreach (var dll in Directory.EnumerateFiles(_pluginDir, "*.dll"))
        {
            try { _loaded.Add(LoadOne(dll)); }
            catch (Exception ex)
            {
                // 记录并继续；单个插件失败不应阻断其他插件
                Console.Error.WriteLine($"[plugin] reject {Path.GetFileName(dll)}: {ex.Message}");
            }
        }
        return _loaded;
    }

    private LoadedPlugin LoadOne(string dllPath)
    {
        IntPtr module = PluginNative.LoadLibraryW(Path.GetFullPath(dllPath));
        if (module == IntPtr.Zero)
            throw new InvalidOperationException($"LoadLibrary failed: {Marshal.GetLastWin32Error()}");

        // 步骤 1：ABI 版本校验，必须是第一个解析的符号
        IntPtr verPtr = PluginNative.GetProcAddress(module, "plugin_abi_version");
        if (verPtr == IntPtr.Zero) { PluginNative.FreeLibrary(module); throw new NotSupportedException("plugin_abi_version missing"); }
        var verFn = Marshal.GetDelegateForFunctionPointer<Func<int>>(verPtr);
        if (verFn() != 1) { PluginNative.FreeLibrary(module); throw new NotSupportedException("ABI version mismatch"); }

        // 步骤 2：描述与初始化
        IntPtr descPtr = PluginNative.GetProcAddress(module, "plugin_describe");
        if (descPtr == IntPtr.Zero) { PluginNative.FreeLibrary(module); throw new NotSupportedException("plugin_describe missing"); }
        var descFn = Marshal.GetDelegateForFunctionPointer<DescribeFn>(descPtr);
        descFn(out IntPtr descJsonPtr);
        // ... 解析 JSON，获取 config_json（从同名 .json 配置文件读取）
        // ... plugin_init 调用
        // ... 解析 execute/destroy/free_string 符号，构造 LoadedPlugin

        return new LoadedPlugin(/* ... */);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DescribeFn(out IntPtr description);
}