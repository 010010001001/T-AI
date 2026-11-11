// src/AiRuntime/AppHost.cs
using System;
using System.IO;
using System.Linq;

/// <summary>
/// 进程级依赖装配：解析数据目录、加载插件、初始化推理后端与模型，
/// 然后按顺序构造逻辑层、记忆层、编排层。
/// </summary>
internal sealed class AppHost : IDisposable
{
    public string BaseDir { get; }
    public string NativeDir { get; }
    public string DataDbPath { get; }
    public string PluginsDir { get; }
    public string? ModelPath { get; private set; }

    public InferenceClient Infer { get; }
    public LogicEngine Logic { get; }
    public MemoryRepository Repo { get; }
    public MemoryCompressor Compressor { get; }
    public FallbackResolver Fallback { get; }
    public ConversationSession Session { get; }
    public PluginManager Plugins { get; }
    public IReadOnlyList<IManagedTool> ManagedTools { get; }
    public ToolPermissionManager Permissions { get; }
    public WorkspaceService Workspace { get; }
    public ConversationOrchestrator Orchestrator { get; }
    public SmokeCommandHandler Smoke { get; }

    /// <summary>活跃上下文的 token 预算（与 MemoryCompressor.ActiveBudget 对齐）。</summary>
    private const int RestoreActiveBudget = 1600;

    /// <summary>长期摘要注入提示词的 token 预算，避免挤占模型 4K 上下文。</summary>
    private const int RestoreSummaryBudget = 900;

    public AppHost(string baseDir, bool autoRestore = true)
    {
        BaseDir = baseDir;
        NativeDir = Path.Combine(baseDir, "native");
        DataDbPath = Path.Combine(baseDir, "data", "knowledge.db");
        PluginsDir = Path.Combine(baseDir, "plugins");

        EnsureDataDirectory();

        Infer = new InferenceClient(_ => { });
        Logic = new LogicEngine();
        Repo = new MemoryRepository(DataDbPath);
        Compressor = new MemoryCompressor(Infer, Logic, Repo);
        Fallback = new FallbackResolver(Repo, Logic);
        Session = new ConversationSession();
        Plugins = new PluginManager(PluginsDir);
        Plugins.LoadAll();

        Workspace = new WorkspaceService(Path.Combine(BaseDir, "data", "agent_workspace.json"));

        ManagedTools = new IManagedTool[]
        {
            new ImageOcrTool(),
            new SpeechAsrTool(),
            new WorkspaceFilesTool(Workspace),
            new WorkspaceEditTool(Workspace),
            new WorkspaceRunTool(Workspace)
        };
        Permissions = new ToolPermissionManager(Path.Combine(BaseDir, "data", "permissions.json"));

        Orchestrator = new ConversationOrchestrator(
            Session, Infer, Logic, Compressor, Repo, Fallback, Plugins, ManagedTools, Permissions);

        Smoke = new SmokeCommandHandler(
            BaseDir, Infer, Logic, Repo, Compressor, Fallback, Orchestrator, Plugins);

        // 跨天持久化：默认恢复当前会话；维护类命令/新会话可跳过
        if (autoRestore) RestoreLatestSession();
        CheckResourceLimits();

        InitializeInference();
    }

    // ------------------------------------------------------------------
    // 会话持久化
    // ------------------------------------------------------------------

    /// <summary>启动时自动恢复当前会话（默认上次会话；新会话则保持空窗口）。</summary>
    private void RestoreLatestSession()
    {
        string? preferred = ReadCurrentSessionId();
        string? target = preferred ?? Repo.GetLatestSessionId();
        if (target is null) return;

        if (!RestoreSession(target, out string reason))
        {
            Console.Error.WriteLine($"[会话] 恢复失败：{reason}");
            return;
        }

        SaveCurrentSessionId(Session.SessionId);
        Console.WriteLine(reason == "新会话"
            ? $"[会话] 已开启空会话 {ShortId(Session.SessionId)}"
            : $"[会话] 已恢复上次对话 {ShortId(Session.SessionId)}（{reason}）");
    }

    private bool RestoreSession(string sessionId, out string reason)
    {
        var rows = Repo.GetRawTurns(sessionId);
        if (rows.Count == 0)
        {
            Session.Restore(sessionId, Array.Empty<DialogueTurn>(), Array.Empty<MemorySummary>(), 0);
            reason = "新会话";
            return true;
        }

        var turns = new List<DialogueTurn>();
        int used = 0;

        // 从最新往前取，直到活跃预算用完；恢复后仍按时间升序
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            string text = StripRolePrefix(row.FullText);
            var role = row.FullText.StartsWith("assistant:", StringComparison.OrdinalIgnoreCase)
                ? DialogueRole.Assistant
                : DialogueRole.User;
            int cost = Infer.CountTokensOrEstimate(text);

            if (used + cost > RestoreActiveBudget && turns.Count > 0) break;

            turns.Add(new DialogueTurn(row.TurnIndex, role, text, row.Timestamp, row.Id));
            used += cost;
        }
        turns.Reverse();

        var summaries = new List<MemorySummary>();
        int summaryUsed = 0;
        foreach (var entry in Repo.GetAllSummaries())
        {
            var summary = new MemorySummary(entry.Level, entry.CompressedText, entry.Timestamp);
            int cost = Infer.CountTokensOrEstimate(FormatSummary(summary));
            if (summaryUsed + cost > RestoreSummaryBudget && summaries.Count > 0) break;
            summaries.Add(summary);
            summaryUsed += cost;
        }

        int nextTurn = rows[^1].TurnIndex + 1;

        // 摘要 + 原文 + 512 token 生成，不应超过模型上下文
        int promptEstimate = used + summaryUsed + 512;
        bool truncated = false;
        if (promptEstimate > 3500)
        {
            truncated = true;
            int drop = 0;
            while (used + summaryUsed + 512 > 3500 && summaries.Count > 0)
            {
                summaryUsed -= Infer.CountTokensOrEstimate(FormatSummary(summaries[^1]));
                summaries.RemoveAt(summaries.Count - 1);
                drop++;
            }
            Console.Error.WriteLine(
                $"[会话] 达到上下文上限（约 {promptEstimate} token > 3500），已保留最近原文并截断 {drop} 条摘要。");
        }

        Session.Restore(sessionId, turns, summaries, nextTurn);
        reason = $"{turns.Count} 轮原文 + {summaries.Count} 条摘要" + (truncated ? "，已截断" : string.Empty);
        return true;
    }

    /// <summary>新开启一轮对话：旧会话仍保存在数据库中，可稍后按时间戳恢复。</summary>
    public void StartNewConversation()
    {
        Session.StartNew();
        SaveCurrentSessionId(Session.SessionId);
        Console.WriteLine("[会话] 已新开启一轮对话。旧会话仍可通过“列出对话”按时间戳恢复。");
    }

    /// <summary>列出可恢复的会话（按最后活跃时间倒序）。</summary>
    public string DescribeSessions()
    {
        var sessions = Repo.ListSessions();
        if (sessions.Count == 0) return "暂无历史会话。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("历史会话（按最后活跃时间倒序）：");
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            sb.AppendLine(
                $"  {i + 1}) {s.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {s.EndedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {s.Turns} 轮  id={ShortId(s.SessionId)}");
        }
        sb.Append("恢复方式：恢复对话 <序号|时间戳|id 前缀>");
        return sb.ToString();
    }

    /// <summary>按序号、时间戳片段或 id 前缀恢复会话。</summary>
    public bool RestoreConversation(string tag, out string message)
    {
        tag = tag.Trim();
        if (tag.Length == 0) { message = "请提供序号、时间戳或 id 前缀。"; return false; }

        var sessions = Repo.ListSessions();
        SessionInfo? match = null;

        if (int.TryParse(tag, out int index) && index >= 1 && index <= sessions.Count)
            match = sessions[index - 1];
        else
            match = sessions.FirstOrDefault(s =>
                s.SessionId.StartsWith(tag, StringComparison.OrdinalIgnoreCase) ||
                s.EndedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm").Contains(tag, StringComparison.OrdinalIgnoreCase) ||
                s.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm").Contains(tag, StringComparison.OrdinalIgnoreCase));

        if (match is null) { message = $"未找到匹配“{tag}”的会话。"; return false; }

        if (!RestoreSession(match.SessionId, out string reason))
        {
            message = "恢复失败：" + reason;
            return false;
        }

        SaveCurrentSessionId(match.SessionId);
        message = $"已恢复会话 {ShortId(match.SessionId)}：{reason}";
        return true;
    }

    /// <summary>处理会话管理命令；识别成功返回 true。</summary>
    public bool TryHandleManagementCommand(string input, out string message)
    {
        message = string.Empty;
        string s = input.Trim().Replace(" ", "");

        if (s.Contains("新开启一轮对话") || s.Contains("新开一轮对话") || s.Contains("新开始一轮对话"))
        {
            StartNewConversation();
            message = "new";
            return true;
        }

        if (s.Equals("列出对话", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("列出会话", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("对话列表"))
        {
            message = DescribeSessions();
            return true;
        }

        if (s.StartsWith("恢复对话") || s.StartsWith("恢复会话"))
        {
            string tag = input.Trim();
            int sp = tag.IndexOfAny(new[] { ' ', '　' });
            tag = sp >= 0 ? tag[(sp + 1)..].Trim() : string.Empty;
            if (tag.Length == 0) { message = "用法：恢复对话 <序号|时间戳|id 前缀>"; return true; }
            message = RestoreConversation(tag, out string detail) ? detail : detail;
            return true;
        }

        return false;
    }

    /// <summary>检查磁盘/数据库/上下文等硬性上限，达到时明确提示用户。</summary>
    public void CheckResourceLimits()
    {
        try
        {
            int maxDbMb = 0;
            if (!int.TryParse(Environment.GetEnvironmentVariable("AI_MAX_DB_MB"), out maxDbMb) || maxDbMb <= 0)
                maxDbMb = 10240;   // 默认 10 GB；512G 磁盘足够多年使用

            if (File.Exists(DataDbPath))
            {
                long sizeMb = new FileInfo(DataDbPath).Length / (1024 * 1024);
                if (sizeMb > maxDbMb)
                    Console.Error.WriteLine($"[上限] 知识库已达 {sizeMb} MB，超过配置上限 {maxDbMb} MB；请归档或调高 AI_MAX_DB_MB。");
            }

            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(DataDbPath))!);
            if (drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
                Console.Error.WriteLine($"[上限] 磁盘剩余空间不足 2 GB（当前 {drive.AvailableFreeSpace / 1024 / 1024} MB），新对话可能无法保存。");

            // 显卡显存：仅在运行时确实要求 GPU 卸载时才检查；CPU-only 构建跳过
            int gpuLayers = 0;
            int.TryParse(Environment.GetEnvironmentVariable("AI_GPU_LAYERS"), out gpuLayers);
            if (gpuLayers != 0)
            {
                int maxVram = 0;
                if (!int.TryParse(Environment.GetEnvironmentVariable("AI_MAX_VRAM_MB"), out maxVram) || maxVram <= 0)
                    maxVram = 7168;   // 8G 卡留约 1G 给系统
                int used = QueryVramUsedMb();
                if (used > 0 && used > maxVram)
                    Console.Error.WriteLine($"[上限] 显存占用 {used} MB 超过配置上限 {maxVram} MB；请降低 AI_GPU_LAYERS 或减少上下文。");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[上限] 资源检查失败：{ex.Message}");
        }
    }

    private static int QueryVramUsedMb()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.used --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return -1;

            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);

            string first = output.Split('\n')[0].Trim();
            return int.TryParse(first, out int mb) ? mb : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string FormatSummary(MemorySummary summary) =>
        $"[L{summary.Level} {summary.At.ToLocalTime():yyyy-MM-dd}] {summary.Text}";

    private static string StripRolePrefix(string text)    {
        int colon = text.IndexOf(':');
        if (colon < 0) return text.Trim();
        string head = text[..colon].Trim();
        return head.Equals("user", StringComparison.OrdinalIgnoreCase) ||
               head.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
               head.Equals("system", StringComparison.OrdinalIgnoreCase)
            ? text[(colon + 1)..].Trim()
            : text.Trim();
    }

    private sealed class SessionState
    {
        public string? Current { get; set; }
    }

    private string StatePath => Path.Combine(BaseDir, "data", "session_state.json");

    private string? ReadCurrentSessionId()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var state = System.Text.Json.JsonSerializer.Deserialize<SessionState>(File.ReadAllText(StatePath));
            return string.IsNullOrWhiteSpace(state?.Current) ? null : state!.Current;
        }
        catch
        {
            return null;
        }
    }

    private void SaveCurrentSessionId(string sessionId)
    {
        try
        {
            File.WriteAllText(StatePath,
                System.Text.Json.JsonSerializer.Serialize(new SessionState { Current = sessionId }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[会话] 保存当前会话指针失败：{ex.Message}");
        }
    }

    private static string ShortId(string id) => id.Length <= 8 ? id : id[..8];

    /// <summary>
    /// 初始化 GGML 后端并尝试加载 models/ 下第一个 GGUF。
    /// 模型缺失不是致命错误：符号、压缩、回退路径仍然可用，只是 LLM 路径降级。
    /// </summary>
    private void InitializeInference()
    {
        string backendsDir = Directory.Exists(Path.Combine(BaseDir, "backends"))
            ? Path.Combine(BaseDir, "backends")
            : NativeDir;

        try
        {
            int rc = Native.ai_backend_init(backendsDir);
            if (rc != 0)
            {
                Console.Error.WriteLine($"[infer] ai_backend_init failed rc={rc}");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[infer] ai_inference.dll 不可用：{ex.Message}");
            return;
        }

        string modelsDir = Path.Combine(BaseDir, "models");
        ModelPath = Directory.Exists(modelsDir)
            ? Directory.GetFiles(modelsDir, "*.gguf")
                .Where(p => new FileInfo(p).Length > 0)
                .OrderBy(p => p)
                .FirstOrDefault()
            : null;

        if (ModelPath is null)
        {
            Console.Error.WriteLine(
                "[infer] 未找到有效的 models/*.gguf（0 字节占位文件会被跳过），LLM 路径降级为不可用。");
            return;
        }

        try
        {
            Infer.LoadModel(ModelPath);
            Console.Error.WriteLine($"[infer] loaded {Path.GetFileName(ModelPath)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[infer] 模型加载失败，LLM 路径降级：{ex.Message}");
        }
    }

    private void EnsureDataDirectory()
    {
        string dataDir = Path.Combine(BaseDir, "data");
        Directory.CreateDirectory(dataDir);

        if (File.Exists(DataDbPath)) return;

        string seed = Path.Combine(dataDir, "knowledge_seed.db");
        if (File.Exists(seed))
        {
            File.Copy(seed, DataDbPath);
        }
        // 种子不存在时由 MemoryRepository 在首次连接时建表
    }

    public void Dispose()
    {
        Plugins.Dispose();
        Permissions.Dispose();
        Infer.Dispose();
    }
}
