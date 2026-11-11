// src/AiRuntime/Plugins/PluginManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int PluginAbiVersionFn();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int PluginDescribeFn(out IntPtr descriptionJson);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int PluginInitFn(
    [MarshalAs(UnmanagedType.LPUTF8Str)] string configJson,
    out IntPtr handle);

/// <summary>
/// 插件加载器。契约见 include/plugin_abi.h：
///   plugin_abi_version / plugin_describe / plugin_init /
///   plugin_execute / plugin_destroy / plugin_free_string（可选）。
/// </summary>
/// <remarks>
/// 单个插件加载失败不会阻断其他插件：失败原因进入 Rejections，
/// 供 smoke plugin-test 断言"ABI 不匹配插件被拒绝"。
/// </remarks>
public sealed class PluginManager : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pluginDir;
    private readonly List<LoadedPlugin> _plugins = new();
    private readonly List<string> _rejections = new();

    public PluginManager(string pluginDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginDir);
        _pluginDir = Path.GetFullPath(pluginDir);
    }

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public int RejectedCount => _rejections.Count;

    public IReadOnlyList<string> Rejections => _rejections;

    /// <summary>扫描插件目录并加载所有 DLL；重复调用只会追加，不重复加载同名插件。</summary>
    public IReadOnlyList<LoadedPlugin> LoadAll()
    {
        if (!Directory.Exists(_pluginDir)) return _plugins;

        foreach (string dll in Directory.EnumerateFiles(_pluginDir, "*.dll").OrderBy(p => p))
        {
            string name = Path.GetFileName(dll);
            if (_plugins.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                _plugins.Add(LoadOne(dll));
            }
            catch (Exception ex)
            {
                _rejections.Add($"{name}: {ex.Message}");
                Console.Error.WriteLine($"[plugin] reject {name}: {ex.Message}");
            }
        }

        return _plugins;
    }

    /// <summary>
    /// 按插件名与函数名查找可执行插件；不存在时返回 null。
    /// 容忍 LLM 把 plugin/function 写反或只给出函数名：先精确匹配，
    /// 再按函数名唯一匹配，最后回退到唯一函数的插件。
    /// </summary>
    public LoadedPlugin? Find(string plugin, string function)
    {
        var exact = _plugins.FirstOrDefault(p =>
            string.Equals(p.Name, plugin, StringComparison.OrdinalIgnoreCase) &&
            p.Descriptor.Functions.Contains(function, StringComparer.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var byFunction = _plugins.FirstOrDefault(p =>
            p.Descriptor.Functions.Contains(function, StringComparer.OrdinalIgnoreCase));
        if (byFunction is not null) return byFunction;

        var byName = _plugins.FirstOrDefault(p =>
            string.Equals(p.Name, plugin, StringComparison.OrdinalIgnoreCase));
        if (byName is not null && byName.Descriptor.Functions.Length == 1) return byName;

        return null;
    }

    private LoadedPlugin LoadOne(string dllPath)
    {
        string fullPath = Path.GetFullPath(dllPath);
        IntPtr module = PluginNative.LoadLibraryW(fullPath);
        if (module == IntPtr.Zero)
            throw new InvalidOperationException(
                $"LoadLibrary failed: Win32={Marshal.GetLastWin32Error()}");

        try
        {
            // 1. ABI 版本必须是第一个解析的符号
            IntPtr verPtr = PluginNative.GetProcAddress(module, "plugin_abi_version");
            if (verPtr == IntPtr.Zero)
                throw new NotSupportedException("plugin_abi_version missing");

            // GetDelegateForFunctionPointer 不接受泛型委托（如 Func<int>），必须用自定义委托
            var versionFn = Marshal.GetDelegateForFunctionPointer<PluginAbiVersionFn>(verPtr);
            if (versionFn() != 1)
                throw new NotSupportedException("ABI version mismatch");

            // 2. 能力描述
            IntPtr descPtr = PluginNative.GetProcAddress(module, "plugin_describe");
            if (descPtr == IntPtr.Zero)
                throw new NotSupportedException("plugin_describe missing");

            var describeFn = Marshal.GetDelegateForFunctionPointer<PluginDescribeFn>(descPtr);
            int descRc = describeFn(out IntPtr descJsonPtr);
            if (descRc != 0 || descJsonPtr == IntPtr.Zero)
                throw new InvalidOperationException($"plugin_describe rc={descRc}");

            string descJson;
            try
            {
                descJson = Marshal.PtrToStringUTF8(descJsonPtr) ?? "{}";
            }
            finally
            {
                FreePluginString(module, descJsonPtr);
            }

            var descriptor = JsonSerializer.Deserialize<PluginDescriptor>(descJson, JsonOptions)
                ?? throw new InvalidOperationException("plugin_describe returned empty json");

            if (string.IsNullOrWhiteSpace(descriptor.Name) || descriptor.Functions is null)
                throw new InvalidOperationException("plugin_describe missing name/functions");

            // 3. 配置：优先读取与 DLL 同名的 .json；不存在时传空对象
            string configPath = Path.ChangeExtension(fullPath, ".json");
            string configJson = File.Exists(configPath) ? File.ReadAllText(configPath) : "{}";

            IntPtr initPtr = PluginNative.GetProcAddress(module, "plugin_init");
            if (initPtr == IntPtr.Zero)
                throw new NotSupportedException("plugin_init missing");

            var initFn = Marshal.GetDelegateForFunctionPointer<PluginInitFn>(initPtr);
            int initRc = initFn(configJson, out IntPtr handle);
            if (initRc != 0 || handle == IntPtr.Zero)
                throw new InvalidOperationException($"plugin_init rc={initRc}");

            // 4. 运行时函数
            var delegates = ResolveDelegates(module);
            return new LoadedPlugin(module, descriptor, handle, delegates);
        }
        catch
        {
            PluginNative.FreeLibrary(module);
            throw;
        }
    }

    private static PluginDelegates ResolveDelegates(IntPtr module)
    {
        IntPtr execPtr = PluginNative.GetProcAddress(module, "plugin_execute");
        IntPtr destroyPtr = PluginNative.GetProcAddress(module, "plugin_destroy");
        IntPtr freePtr = PluginNative.GetProcAddress(module, "plugin_free_string");

        if (execPtr == IntPtr.Zero) throw new NotSupportedException("plugin_execute missing");
        if (destroyPtr == IntPtr.Zero) throw new NotSupportedException("plugin_destroy missing");
        if (freePtr == IntPtr.Zero) throw new NotSupportedException("plugin_free_string missing");

        return new PluginDelegates(
            execute: Marshal.GetDelegateForFunctionPointer<PluginExecuteFn>(execPtr),
            destroy: Marshal.GetDelegateForFunctionPointer<PluginDestroyFn>(destroyPtr),
            freeString: Marshal.GetDelegateForFunctionPointer<PluginFreeStringFn>(freePtr));
    }

    /// <summary>
    /// plugin_describe 返回的字符串由插件分配。若插件导出了 plugin_free_string 就用它，
    /// 否则只能泄漏这一小段描述字符串（ABI v1 之前的插件）。
    /// </summary>
    private static void FreePluginString(IntPtr module, IntPtr s)
    {
        if (s == IntPtr.Zero) return;

        IntPtr freePtr = PluginNative.GetProcAddress(module, "plugin_free_string");
        if (freePtr == IntPtr.Zero) return;

        var freeFn = Marshal.GetDelegateForFunctionPointer<PluginFreeStringFn>(freePtr);
        freeFn(s);
    }

    public void Dispose()
    {
        foreach (var plugin in _plugins)
        {
            try { plugin.Dispose(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[plugin] dispose failed: {ex.Message}");
            }
        }
        _plugins.Clear();
    }
}
