// src/AiRuntime/Program.cs
using System.Reflection;
using System.Runtime.InteropServices;

var baseDir = AppContext.BaseDirectory;
var nativeDir = Path.Combine(baseDir, "native");

// 所有原生 DLL 都从 native/ 解析，避免 PATH 污染与 DLL 劫持。
// 返回 IntPtr.Zero 表示交给运行时默认搜索顺序继续处理。
NativeLibrary.SetDllImportResolver(
    Assembly.GetExecutingAssembly(),
    (name, assembly, searchPath) =>
    {
        foreach (string dir in new[] { nativeDir, baseDir })
        {
            string candidate = Path.Combine(dir, name + ".dll");
            if (File.Exists(candidate)) return NativeLibrary.Load(candidate);
        }
        return IntPtr.Zero;
    });

// 关键：logic_engine.dll / ai_inference.dll 依赖同目录的其他 DLL（sympy_engine、ggml、llama）。
// Windows 加载器默认只在 exe 目录搜索，不会去 native/ 找依赖，因此先显式预加载。
if (Directory.Exists(nativeDir))
{
    // CUDA 运行库先于 ggml-cuda 加载，避免依赖搜索路径差异导致静默回退 CPU
    foreach (string pattern in new[] { "cudart64_*.dll", "cublasLt64_*.dll", "cublas64_*.dll" })
    {
        foreach (string dll in Directory.GetFiles(nativeDir, pattern))
        {
            try { NativeLibrary.Load(dll); } catch (Exception) { }
        }
    }

    foreach (string dep in new[]
             {
                 "sympy_engine.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll",
                 "ggml-cuda.dll", "llama.dll"
             })
    {
        string path = Path.Combine(nativeDir, dep);
        if (!File.Exists(path)) continue;
        try { NativeLibrary.Load(path); }
        catch (Exception) { /* 非必需依赖缺失不应阻断启动 */ }
    }
}

// 维护类命令不需要恢复会话（也避免启动时加载模型/打印无关提示）
bool autoRestore = args.Length == 0 || !(args[0] is "--tool" or "--list-sessions" or "--new-session"
    or "--restore-session" or "--set-workspace" or "--set-permission" or "--workspace-status");
using var host = new AppHost(baseDir, autoRestore);
Environment.ExitCode = await RunAsync(host, args);

static async Task<int> RunAsync(AppHost host, string[] args)
{
    if (args.Length > 0 && args[0] == "--smoke")
        return await host.Smoke.RunAsync(args.Skip(1).ToArray());

    if (args.Length > 0 && args[0] == "--version")
    {
        Console.WriteLine("AiRuntime 0.1.0");
        return 0;
    }

    // 会话管理（跨天持久化）
    if (args.Length > 0 && args[0] == "--new-session")
    {
        host.StartNewConversation();
        return 0;
    }

    if (args.Length > 0 && args[0] == "--list-sessions")
    {
        Console.WriteLine(host.DescribeSessions());
        return 0;
    }

    string? restoreTag = GetArg(args, "--restore-session");
    if (restoreTag is not null)
    {
        if (!host.RestoreConversation(restoreTag, out string detail))
        {
            Console.Error.WriteLine(detail);
            return 2;
        }
        Console.WriteLine(detail);
        return 0;
    }

    // 工作区与权限（自主代码工具）
    string? setWorkspace = GetArg(args, "--set-workspace");
    if (setWorkspace is not null)
    {
        Console.WriteLine(host.Workspace.SetRoot(setWorkspace)
            ? host.Workspace.Status()
            : "设置失败：路径不存在或不是绝对路径。");
        return host.Workspace.Root is null ? 2 : 0;
    }

    string? setPermission = GetArg(args, "--set-permission");
    if (setPermission is not null)
    {
        if (!int.TryParse(setPermission, out int level) || level is < 1 or > 3)
        {
            Console.Error.WriteLine("用法：--set-permission 1|2|3");
            return 2;
        }
        return host.Workspace.SetLevel(level) ? 0 : 3;
    }

    if (args.Length > 0 && args[0] == "--workspace-status")
    {
        Console.WriteLine(host.Workspace.Status());
        return 0;
    }

    // 直接调用托管工具（OCR / 语音识别 / 工作区），便于脱离 LLM 调试与脚本化使用
    string? toolName = GetArg(args, "--tool");
    if (toolName is not null)
    {
        var tool = host.ManagedTools.FirstOrDefault(t =>
            t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        if (tool is null) { Console.Error.WriteLine($"未知工具: {toolName}"); return 2; }

        if (tool.RequiresPermission && !host.Permissions.EnsureAllowed(tool.Name))
        {
            Console.Error.WriteLine("需要权限，已取消。");
            return 3;
        }

        try
        {
            string fn = GetArg(args, "--function") ?? tool.Functions[0];
            string payload = GetArg(args, "--args") ?? "{}";
            string result = tool.Execute(fn, payload);
            host.Permissions.Touch(tool.Name);
            Console.WriteLine(result);
            return result.Contains("\"error\"", StringComparison.OrdinalIgnoreCase) ? 4 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    string? input = GetArg(args, "--input") ?? GetArg(args, "--chat");
    if (input is not null)
    {
        if (host.Permissions.TryHandleCommand(input)) return 0;
        if (host.Workspace.TryHandleCommand(input, out string workspaceMessage))
        {
            Console.WriteLine(workspaceMessage);
            return 0;
        }
        if (host.TryHandleManagementCommand(input, out string management))
        {
            if (management != "new") Console.WriteLine(management);
            return 0;
        }

        try
        {
            string response = await host.Orchestrator.HandleUserInputAsync(input, CancellationToken.None);
            Console.WriteLine(response);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    Console.WriteLine("AiRuntime 0.1.0 — 输入 exit 退出。");
    while (true)
    {
        Console.Write("> ");
        string? line = Console.ReadLine();
        if (line is null) return 0;

        line = line.Trim();
        if (line.Length == 0) continue;
        if (line is "exit" or "quit") return 0;

        // 权限与对话管理命令先于模型路由处理
        if (host.Permissions.TryHandleCommand(line)) continue;
        if (host.Workspace.TryHandleCommand(line, out string workspaceMessage))
        {
            Console.WriteLine(workspaceMessage);
            continue;
        }
        if (host.TryHandleManagementCommand(line, out string management))
        {
            if (management != "new") Console.WriteLine(management);
            continue;
        }

        try
        {
            string response = await host.Orchestrator.HandleUserInputAsync(line, CancellationToken.None);
            Console.WriteLine(response);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
        }
    }
}

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}
