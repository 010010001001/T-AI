// src/AiRuntime/Tools/WorkspaceTools.cs
using System.Text.Json;

/// <summary>
/// 自主工具：工作区文件浏览（权限 1）。输入 {"path":"."}。
/// </summary>
public sealed class WorkspaceFilesTool : IManagedTool
{
    private readonly WorkspaceService _workspace;

    public WorkspaceFilesTool(WorkspaceService workspace) => _workspace = workspace;

    public string Name => "workspace_files";
    public string[] Functions => new[] { "list", "read" };
    public bool RequiresPermission => false;

    public string Execute(string function, string inputJson)
    {
        string path = ".";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            if (doc.RootElement.TryGetProperty("path", out var p)) path = p.GetString() ?? path;
        }
        catch (JsonException) { return Error("invalid json"); }

        return function.ToLowerInvariant() switch
        {
            "list" => _workspace.ListFiles(path, out _),
            "read" => _workspace.ReadFile(path, out _),
            _ => Error($"unsupported function: {function}")
        };
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}

/// <summary>
/// 自主工具：修改工作区文件（权限 2）。输入 {"path":"...","content":"...","append":false}。
/// </summary>
public sealed class WorkspaceEditTool : IManagedTool
{
    private readonly WorkspaceService _workspace;

    public WorkspaceEditTool(WorkspaceService workspace) => _workspace = workspace;

    public string Name => "workspace_edit";
    public string[] Functions => new[] { "write", "append" };
    public bool RequiresPermission => false;

    public string Execute(string function, string inputJson)
    {
        string path = string.Empty;
        string content = string.Empty;
        bool append = function.Equals("append", StringComparison.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            if (doc.RootElement.TryGetProperty("path", out var p)) path = p.GetString() ?? string.Empty;
            if (doc.RootElement.TryGetProperty("content", out var c)) content = c.GetString() ?? string.Empty;
            if (doc.RootElement.TryGetProperty("append", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False)
                append = a.GetBoolean();
        }
        catch (JsonException) { return Error("invalid json"); }

        if (path.Length == 0) return Error("path is required");
        return _workspace.WriteFile(path, content, append, out _);
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}

/// <summary>
/// 自主工具：执行命令（权限 3）。输入 {"command":"...","timeout_ms":60000}。
/// 以当前用户身份运行，不自动提权；需要管理员时会返回处理步骤。
/// </summary>
public sealed class WorkspaceRunTool : IManagedTool
{
    private readonly WorkspaceService _workspace;

    public WorkspaceRunTool(WorkspaceService workspace) => _workspace = workspace;

    public string Name => "workspace_run";
    public string[] Functions => new[] { "run" };
    public bool RequiresPermission => false;

    public string Execute(string function, string inputJson)
    {
        if (!function.Equals("run", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = $"unsupported function: {function}" });

        string command = string.Empty;
        int timeoutMs = 60000;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            if (doc.RootElement.TryGetProperty("command", out var c)) command = c.GetString() ?? string.Empty;
            if (doc.RootElement.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out int ms)) timeoutMs = ms;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { error = "invalid json" });
        }

        if (command.Length == 0)
            return JsonSerializer.Serialize(new { error = "command is required" });

        return _workspace.RunCommand(command, timeoutMs, out _);
    }
}
