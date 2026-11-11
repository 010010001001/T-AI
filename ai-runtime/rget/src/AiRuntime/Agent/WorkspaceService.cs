// src/AiRuntime/Agent/WorkspaceService.cs
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>
/// 自主工具的工作区与三档权限：
///   1 = 只浏览工作区文件（列目录、读文件）
///   2 = 仅修改工作区文件（写/追加，仍限定在工作区内）
///   3 = 完全权限（读写任意路径、以当前用户权限执行命令；不做提权）
///
/// 首次使用必须指定工作区并保存；未指定时拒绝执行。选定工作区后默认类型 1。
/// 所有比较都基于 Path.GetFullPath 后的绝对路径，阻止 ../ 越界。
/// </summary>
public sealed class WorkspaceService
{
    public const int LevelBrowse = 1;
    public const int LevelEdit = 2;
    public const int LevelFull = 3;

    private sealed class State
    {
        public string? Root { get; set; }
        public int Level { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }

    private readonly string _configPath;
    private readonly object _lock = new();
    private string? _root;
    private int _level;

    public WorkspaceService(string configPath)
    {
        _configPath = configPath;
        Load();
    }

    public string? Root { get { lock (_lock) return _root; } }
    public int Level { get { lock (_lock) return _level; } }

    public string Status()
    {
        lock (_lock)
        {
            if (_root is null) return "工作区：未指定\n权限：未启用（首次使用代码工具时会要求指定工作区）";
            return $"工作区：{_root}\n权限：{_level}（{LevelName(_level)}）";
        }
    }

    public static string LevelName(int level) => level switch
    {
        LevelBrowse => "只浏览工作区文件",
        LevelEdit => "仅修改工作区文件",
        LevelFull => "完全权限（当前用户，不提权）",
        _ => "未启用"
    };

    // ------------------------------------------------------------------
    // 配置命令
    // ------------------------------------------------------------------

    public bool TryHandleCommand(string input, out string message)
    {
        message = string.Empty;
        string s = input.Trim();

        if (s.Contains("工作区状态") || s.Contains("工作区路径"))
        {
            message = Status();
            return true;
        }

        if (s.StartsWith("设置工作区") || s.StartsWith("指定工作区") || s.StartsWith("工作区设置为"))
        {
            string path = ExtractArgument(s);
            if (path.Length == 0) { message = "用法：设置工作区 <绝对路径>"; return true; }
            message = SetRoot(path) ? "工作区已保存：" + Root : "工作区设置失败：目录不存在或不是绝对路径";
            return true;
        }

        if (s.StartsWith("设置权限") || s.StartsWith("权限") || s == "只读权限" || s == "编辑权限" || s == "完全权限")
        {
            int level = s switch
            {
                "只读权限" => LevelBrowse,
                "编辑权限" => LevelEdit,
                "完全权限" => LevelFull,
                _ => ParseLevel(s)
            };
            if (level == 0) { message = "用法：权限 1|2|3（1=只浏览，2=可修改，3=完全权限）"; return true; }
            message = SetLevel(level) ? $"权限已设置为 {level}（{LevelName(level)}）" : "请先设置工作区，再修改权限。";
            return true;
        }

        return false;
    }

    /// <summary>设置/切换工作区；成功后若尚未设置权限，默认类型 1。</summary>
    public bool SetRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Path.IsPathRooted(path)) return false;
        if (!Directory.Exists(path)) return false;

        lock (_lock)
        {
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            // 每次选定工作区都回到默认类型 1；更高权限必须由用户显式切换
            _level = LevelBrowse;
            SaveLocked();
        }
        Console.WriteLine($"[工作区] 已保存：{_root}（默认权限 1：只浏览工作区文件）");
        return true;
    }

    public bool SetLevel(int level)
    {
        if (level is < LevelBrowse or > LevelFull) return false;
        lock (_lock)
        {
            if (_root is null) return false;
            _level = level;
            SaveLocked();
        }
        if (level == LevelFull)
            Console.WriteLine("[工作区] 完全权限：以当前用户身份读写任意路径并执行命令；不会自动提权。");
        return true;
    }

    // ------------------------------------------------------------------
    // 工具操作
    // ------------------------------------------------------------------

    /// <summary>首次使用时要求指定工作区；非交互环境直接失败。</summary>
    public bool EnsureReady(int requiredLevel, out string error)
    {
        error = string.Empty;
        lock (_lock)
        {
            if (_root is not null)
            {
                if (_level < requiredLevel)
                {
                    error = $"当前权限为 {_level}（{LevelName(_level)}），该操作需要权限 {requiredLevel}（{LevelName(requiredLevel)}）。请使用“权限 {requiredLevel}”切换。";
                    return false;
                }
                return true;
            }
        }

        if (Console.IsInputRedirected)
        {
            error = "尚未指定工作区，且当前为非交互环境。请先运行：设置工作区 <绝对路径>";
            return false;
        }

        Console.Write("[工作区] 首次使用代码工具，请输入工作区绝对路径（留空取消）：");
        string? path = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(path) || !SetRoot(path))
        {
            error = "未指定有效工作区，已拒绝执行。请使用：设置工作区 <绝对路径>";
            return false;
        }

        lock (_lock)
        {
            // 首次设置工作区只开启默认权限 1；更高权限必须由用户显式切换
            return _level >= requiredLevel;
        }
    }

    public string ListFiles(string relativePath, out string error)
    {
        error = string.Empty;
        if (!EnsureReady(LevelBrowse, out error)) return Error(error);

        string target;
        lock (_lock)
        {
            if (!TryResolve(relativePath, mustInside: _level < LevelFull, out target, out error)) return Error(error);
        }

        try
        {
            if (!Directory.Exists(target)) { error = $"目录不存在：{target}"; return Error(error); }

            var items = new List<object>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(target).Take(200))
            {
                var info = new FileInfo(entry);
                bool isDir = Directory.Exists(entry);
                items.Add(new
                {
                    name = Path.GetFileName(entry),
                    type = isDir ? "dir" : "file",
                    size = isDir ? 0 : info.Length,
                    modified = info.LastWriteTimeUtc.ToString("O")
                });
            }

            return JsonSerializer.Serialize(new { path = target, count = items.Count, entries = items });
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return Error(error);
        }
    }

    public string ReadFile(string path, out string error)
    {
        error = string.Empty;
        if (!EnsureReady(LevelBrowse, out error)) return Error(error);

        string target;
        lock (_lock)
        {
            if (!TryResolve(path, mustInside: _level < LevelFull, out target, out error)) return Error(error);
        }

        try
        {
            if (!File.Exists(target)) { error = $"文件不存在：{target}"; return Error(error); }
            var info = new FileInfo(target);
            if (info.Length > 1024 * 1024) { error = "文件超过 1 MB，拒绝直接读取。"; return Error(error); }

            string text = File.ReadAllText(target);
            return JsonSerializer.Serialize(new { path = target, bytes = info.Length, content = text });
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return Error(error);
        }
    }

    public string WriteFile(string path, string content, bool append, out string error)
    {
        error = string.Empty;
        if (!EnsureReady(LevelEdit, out error)) return Error(error);

        string target;
        lock (_lock)
        {
            if (!TryResolve(path, mustInside: _level < LevelFull, out target, out error)) return Error(error);
        }

        try
        {
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (append) File.AppendAllText(target, content);
            else File.WriteAllText(target, content);

            return JsonSerializer.Serialize(new
            {
                path = target,
                bytes = new FileInfo(target).Length,
                mode = append ? "append" : "write"
            });
        }
        catch (UnauthorizedAccessException)
        {
            error = "写入需要管理员权限。本工具不会自动提权；请以管理员身份打开编辑器/终端，手动写入该路径。";
            return Error(error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return Error(error);
        }
    }

    public string RunCommand(string command, int timeoutMs, out string error)
    {
        error = string.Empty;
        if (!EnsureReady(LevelFull, out error)) return Error(error);

        string? root;
        lock (_lock) root = _root;

        if (string.IsNullOrWhiteSpace(command)) { error = "命令为空。"; return Error(error); }
        if (timeoutMs <= 0) timeoutMs = 60000;
        if (timeoutMs > 600000) timeoutMs = 600000;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c " + command,
                WorkingDirectory = root!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 cmd.exe");

            var stdoutTask = Task.Run(() => ReadAllBytes(process.StandardOutput.BaseStream));
            var stderrTask = Task.Run(() => ReadAllBytes(process.StandardError.BaseStream));

            bool exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                error = $"命令超时（{timeoutMs} ms），已终止。";
                return Error(error);
            }

            string stdout = DecodeOutput(stdoutTask.GetAwaiter().GetResult());
            string stderr = DecodeOutput(stderrTask.GetAwaiter().GetResult());
            int exitCode = process.ExitCode;

            string combined = stdout + stderr;
            bool denied = exitCode == 5 ||
                          combined.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
                          combined.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                          combined.Contains("发生 5 错误", StringComparison.OrdinalIgnoreCase) ||
                          combined.Contains("系统错误 5", StringComparison.OrdinalIgnoreCase) ||
                          combined.Contains("elevation", StringComparison.OrdinalIgnoreCase);

            if (denied)
            {
                error = "命令需要管理员权限。本工具不会自动提权。\n" +
                        "处理步骤：\n" +
                        "1. 关闭本程序；\n" +
                        "2. 右键“命令提示符”或“PowerShell”，选择“以管理员身份运行”；\n" +
                        $"3. 执行：cd /d \"{root}\" 然后重新运行该命令。\n" +
                        $"命令：{command}";
                return Error(error);
            }

            return JsonSerializer.Serialize(new
            {
                exit_code = exitCode,
                stdout = Truncate(stdout),
                stderr = Truncate(stderr)
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return Error(error);
        }
    }

    // ------------------------------------------------------------------

    private bool TryResolve(string path, bool mustInside, out string full, out string error)
    {
        full = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path)) path = ".";

        try
        {
            string root = _root!;
            full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

            if (mustInside && !IsInside(root, full))
            {
                error = $"路径越界：{full} 不在工作区 {root} 内。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsInside(string root, string candidate)
    {
        string r = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(r, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.TrimEndingDirectorySeparator(candidate), Path.TrimEndingDirectorySeparator(root),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string s) => s.Length <= 20000 ? s : s[..20000] + "\n...[截断]";

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });

    // ------------------------------------------------------------------
    // 命令输出解码：cmd 内建命令输出 UTF-16/GBK 混杂，外部程序多为 OEM 代码页
    // ------------------------------------------------------------------

    private static readonly Lazy<Encoding> OemEncoding = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.Default;
        }
    });

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string DecodeOutput(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        // 纯 ASCII 直接返回，避免不必要的编码判断
        if (Array.TrueForAll(bytes, b => b < 0x80)) return Encoding.ASCII.GetString(bytes);

        // 先尝试严格 UTF-8；失败则按系统 OEM 代码页（中文 Windows 为 GBK）
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return OemEncoding.Value.GetString(bytes);
        }
    }

    private static string ExtractArgument(string text)
    {
        string rest = text
            .Replace("设置工作区", "", StringComparison.OrdinalIgnoreCase)
            .Replace("指定工作区", "", StringComparison.OrdinalIgnoreCase)
            .Replace("工作区设置为", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (rest.StartsWith("\"") || rest.StartsWith("'"))
        {
            char quote = rest[0];
            int end = rest.IndexOf(quote, 1);
            return end > 0 ? rest[1..end].Trim() : rest.Trim(quote).Trim();
        }

        return rest.Trim();
    }

    private static int ParseLevel(string text)
    {
        foreach (char c in text)
            if (c is >= '1' and <= '3') return c - '0';
        return 0;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_configPath));
            if (state?.Root is null) return;
            if (!Directory.Exists(state.Root)) return;

            _root = state.Root;
            _level = state.Level is >= LevelBrowse and <= LevelFull ? state.Level : LevelBrowse;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[工作区] 读取配置失败：{ex.Message}");
        }
    }

    private void SaveLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(_configPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(_configPath, JsonSerializer.Serialize(new State
            {
                Root = _root,
                Level = _level,
                UpdatedUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[工作区] 保存失败：{ex.Message}");
        }
    }
}
