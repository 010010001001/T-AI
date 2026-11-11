// src/AiRuntime/Security/ToolPermissionManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

/// <summary>
/// 敏感工具权限管理：图像 OCR、语音识别。
///
/// 规则（来自需求）：
///   - 开启权限必须询问用户；
///   - 任务结束后 30 分钟无使用自动关闭；
///   - 用户可“保持”（跨进程重启持久）或“关闭”；
///   - 未声明保持时，超时即关闭。
/// </summary>
public sealed class ToolPermissionManager : IDisposable
{
    public static readonly TimeSpan AutoCloseAfter = TimeSpan.FromMinutes(30);

    private sealed class Entry
    {
        public bool Enabled { get; set; }
        public bool Kept { get; set; }
        public DateTimeOffset LastUsedUtc { get; set; }
    }

    private sealed class Store
    {
        public Dictionary<string, Entry> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private int _disposed;

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image_ocr"] = "图像识别（Windows OCR）",
        ["speech_asr"] = "语音识别（Windows SAPI）",
        ["web_search"] = "联网搜索（Bing）"
    };

    public ToolPermissionManager(string permissionsPath)
    {
        _path = permissionsPath;
        Load();
        ExpireIfNeeded(notify: false);

        // 每 30 秒检查一次超时；分钟级精度足够，避免为每个工具建独立定时器
        _timer = new Timer(_ => ExpireIfNeeded(notify: true), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public bool IsKept(string tool)
    {
        lock (_lock) return Get(tool).Kept;
    }

    public bool IsEnabled(string tool)
    {
        lock (_lock)
        {
            var e = Get(tool);
            return e.Enabled && (e.Kept || DateTimeOffset.UtcNow - e.LastUsedUtc < AutoCloseAfter);
        }
    }

    /// <summary>
    /// 执行敏感工具前的守门。返回 true 表示允许本次调用。
    /// 非交互环境（如脚本）且未保持权限时拒绝，避免静默提权。
    /// </summary>
    public bool EnsureAllowed(string tool)
    {
        string display = DisplayNames.TryGetValue(tool, out var d) ? d : tool;

        lock (_lock)
        {
            var e = Get(tool);
            if (e.Kept)
            {
                e.Enabled = true;
                e.LastUsedUtc = DateTimeOffset.UtcNow;
                SaveLocked();
                return true;
            }

            if (e.Enabled && DateTimeOffset.UtcNow - e.LastUsedUtc < AutoCloseAfter)
            {
                e.LastUsedUtc = DateTimeOffset.UtcNow;
                SaveLocked();
                return true;
            }

            e.Enabled = false;
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"[权限] {display} 未开启；非交互环境无法询问，已拒绝。");
            return false;
        }

        Console.WriteLine($"[权限] 需要使用{display}。");
        Console.WriteLine("  [y] 仅本次    [k] 保持开启（跨重启）    [n] 拒绝（默认）");
        Console.Write("选择: ");
        string? answer = Console.ReadLine();
        string choice = (answer ?? "n").Trim().ToLowerInvariant();

        if (choice is not ("y" or "k"))
        {
            Console.WriteLine("[权限] 已拒绝。");
            return false;
        }

        lock (_lock)
        {
            var e = Get(tool);
            e.Enabled = true;
            e.Kept = choice == "k";
            e.LastUsedUtc = DateTimeOffset.UtcNow;
            SaveLocked();
        }

        Console.WriteLine(choice == "k"
            ? $"[权限] {display} 已保持开启（重启后仍有效）。"
            : $"[权限] {display} 已允许本次使用；任务结束 30 分钟后自动关闭。");
        return true;
    }

    /// <summary>每次成功/失败执行敏感工具后调用，重置 30 分钟计时起点。</summary>
    public void Touch(string tool)
    {
        lock (_lock)
        {
            var e = Get(tool);
            if (!e.Enabled) return;
            e.LastUsedUtc = DateTimeOffset.UtcNow;
            SaveLocked();
        }
    }

    public bool Keep(string tool)
    {
        string display = DisplayNames.TryGetValue(tool, out var d) ? d : tool;
        lock (_lock)
        {
            var e = Get(tool);
            e.Enabled = true;
            e.Kept = true;
            e.LastUsedUtc = DateTimeOffset.UtcNow;
            SaveLocked();
        }
        Console.WriteLine($"[权限] {display} 已保持开启（跨重启有效）。");
        return true;
    }

    public bool Close(string tool)
    {
        string display = DisplayNames.TryGetValue(tool, out var d) ? d : tool;
        lock (_lock)
        {
            var e = Get(tool);
            e.Enabled = false;
            e.Kept = false;
            SaveLocked();
        }
        Console.WriteLine($"[权限] {display} 已关闭。");
        return true;
    }

    public string Describe()
    {
        lock (_lock)
        {
            var lines = new List<string>();
            foreach (var tool in _tools.Keys.OrderBy(k => k))
            {
                var e = _tools[tool];
                string name = DisplayNames.TryGetValue(tool, out var d) ? d : tool;
                string state = e.Kept ? "保持开启"
                    : e.Enabled ? $"本次开启（{Math.Max(0, (AutoCloseAfter - (DateTimeOffset.UtcNow - e.LastUsedUtc)).TotalMinutes):0} 分钟后自动关闭）"
                    : "关闭";
                lines.Add($"{name}: {state}");
            }
            return lines.Count == 0 ? "暂无敏感权限记录。" : string.Join("\n", lines);
        }
    }

    /// <summary>
    /// 处理权限相关的聊天命令。识别成功返回 true，并写出提示。
    /// </summary>
    public bool TryHandleCommand(string input)
    {
        string s = input.Trim().Replace(" ", "");
        if (s.Length == 0) return false;

        if (s.Contains("权限状态") || s == "权限") { Console.WriteLine(Describe()); return true; }

        if (s.Contains("联网") || s.Contains("网络") || s.Contains("上网") || s.Contains("搜索权限"))
        {
            if (s.Contains("关闭") || s.Contains("禁用")) return Close("web_search");
            if (s.Contains("保持") || s.Contains("开启") || s.Contains("允许")) return Keep("web_search");
        }

        if (s.Contains("图像") || s.Contains("图片") || s.Contains("OCR") || s.Contains("ocr"))
        {
            if (s.Contains("关闭") || s.Contains("禁用")) return Close("image_ocr");
            if (s.Contains("保持") || s.Contains("开启") || s.Contains("允许")) return Keep("image_ocr");
        }

        if (s.Contains("语音") || s.Contains("麦克风"))
        {
            if (s.Contains("关闭") || s.Contains("禁用")) return Close("speech_asr");
            if (s.Contains("保持") || s.Contains("开启") || s.Contains("允许")) return Keep("speech_asr");
        }

        return false;
    }

    // ------------------------------------------------------------------

    private Entry Get(string tool)
    {
        if (!_tools.TryGetValue(tool, out var e))
        {
            e = new Entry();
            _tools[tool] = e;
        }
        return e;
    }

    private void ExpireIfNeeded(bool notify)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        lock (_lock)
        {
            bool changed = false;
            var now = DateTimeOffset.UtcNow;

            foreach (var pair in _tools)
            {
                var e = pair.Value;
                if (!e.Enabled || e.Kept) continue;
                if (now - e.LastUsedUtc < AutoCloseAfter) continue;

                e.Enabled = false;
                changed = true;

                if (notify)
                {
                    string name = DisplayNames.TryGetValue(pair.Key, out var d) ? d : pair.Key;
                    Console.Error.WriteLine($"[权限] {name} 任务结束已超过 30 分钟，权限自动关闭。");
                }
            }

            if (changed) SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_path));
            if (store?.Tools is null) return;

            foreach (var pair in store.Tools)
                _tools[pair.Key] = pair.Value;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[权限] 读取失败，按默认关闭处理：{ex.Message}");
            _tools.Clear();
        }
    }

    private void SaveLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var store = new Store { Tools = new Dictionary<string, Entry>(_tools, StringComparer.OrdinalIgnoreCase) };
            File.WriteAllText(_path, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[权限] 保存失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
    }
}
