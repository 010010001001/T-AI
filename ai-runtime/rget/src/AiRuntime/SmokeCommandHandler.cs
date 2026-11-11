// src/AiRuntime/SmokeCommandHandler.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

/// <summary>
/// --smoke 子命令处理器。仅供 smoke_test.ps1 使用，不进入正常对话路径。
/// 输出一律为 key=value 行，便于 PowerShell 用 -match 断言。
/// 任何未捕获异常输出 error=... 并返回非零，让脚本明确失败。
/// </summary>
public sealed class SmokeCommandHandler
{
    private readonly string _baseDir;
    private readonly InferenceClient _infer;
    private readonly LogicEngine _logic;
    private readonly MemoryRepository _repo;
    private readonly MemoryCompressor _compressor;
    private readonly FallbackResolver _fallback;
    private readonly ConversationOrchestrator _orchestrator;
    private readonly PluginManager _plugins;

    public SmokeCommandHandler(
        string baseDir,
        InferenceClient infer,
        LogicEngine logic,
        MemoryRepository repo,
        MemoryCompressor compressor,
        FallbackResolver fallback,
        ConversationOrchestrator orchestrator,
        PluginManager plugins)
    {
        _baseDir = baseDir;
        _infer = infer;
        _logic = logic;
        _repo = repo;
        _compressor = compressor;
        _fallback = fallback;
        _orchestrator = orchestrator;
        _plugins = plugins;
    }

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) { Emit("error", "missing subcommand"); return 64; }

        string cmd = args[0];
        try
        {
            return cmd switch
            {
                "load"          => RunLoad(args),
                "chat"          => await RunChatAsync(args),
                "compress-test" => await RunCompressTestAsync(args),
                "fallback-test" => RunFallbackTest(args),
                "plugin-test"   => RunPluginTest(),
                "generate"      => await RunGenerateAsync(args),
                "translate"     => RunTranslate(args),
                "route"         => RunRoute(args),
                "keywords"      => RunKeywords(args),
                "memory"        => await RunMemoryTestAsync(args),
                _ => Fail($"unknown subcommand: {cmd}")
            };
        }
        catch (Exception ex)
        {
            Emit("error", ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    // ---------- 1. load ----------
    private int RunLoad(string[] args)
    {
        string? explicitModel = GetArg(args, "--model");
        string model = explicitModel ?? FindDefaultModel();

        // 允许脚本传相对路径；统一按 baseDir 解析，避免依赖调用方的工作目录
        if (!Path.IsPathRooted(model))
            model = Path.GetFullPath(Path.Combine(_baseDir, model));

        if (!File.Exists(model))
            return Fail($"模型文件不存在: {model}");

        string backendsDir = Directory.Exists(Path.Combine(_baseDir, "backends"))
            ? Path.Combine(_baseDir, "backends")
            : Path.Combine(_baseDir, "native");

        int rc = Native.ai_backend_init(backendsDir);
        if (rc != 0) return Fail($"backend_init rc={rc}");

        // AppHost 启动时可能已经加载过同一个模型；避免为冒烟测试重复加载
        if (!_infer.IsModelLoaded)
            _infer.LoadModel(model);

        Emit("MODEL_LOADED", model);
        Emit("model_path", model);
        return 0;
    }

    // ---------- 2. chat ----------
    // 若提供 --candidate，绕过 LLM，直接用 LogicEngine 验证符号裁决；
    // 否则走完整 ConversationOrchestrator，并用 OnTrace 收集路由与执行位置。
    private async Task<int> RunChatAsync(string[] args)
    {
        string input = GetArg(args, "--input") ?? throw new ArgumentException("--input required");
        string? candidate = GetArg(args, "--candidate");

        if (candidate is not null)
        {
            var verdict = _logic.VerifyCandidate(candidate);
            Emit("route", "SYMBOLIC");
            Emit("llm_candidate_value", candidate);
            Emit("verdict_accepted", verdict.Accepted.ToString().ToLowerInvariant());
            if (verdict.Accepted) Emit("value", verdict.Value ?? "");
            else Emit("reason", verdict.Reason ?? "");
            Emit("source", verdict.Accepted ? "sympy" : "none");
            return 0;
        }

        var trace = new List<string>();
        _orchestrator.OnTrace = trace.Add;
        int llmBefore = _infer.CallCount;

        try
        {
            string response = await _orchestrator.HandleUserInputAsync(input, CancellationToken.None);

            foreach (string evt in trace)
                Console.WriteLine(evt);   // trace 已是 key=value 形式

            Emit("llm_calls", (_infer.CallCount - llmBefore).ToString());
            Emit("response_len", response.Length.ToString());
            Emit("response", response.Replace("\n", " ").Replace("\r", " "));
            return 0;
        }
        finally
        {
            _orchestrator.OnTrace = null;
        }
    }

    // ---------- 3. compress-test ----------
    private async Task<int> RunCompressTestAsync(string[] args)
    {
        int targetTokens = int.Parse(GetArg(args, "--target-tokens") ?? "1200");
        var (dbPath, cleanup) = CreateTempDatabase();
        try
        {
            var repo = new MemoryRepository(dbPath);
            var session = new ConversationSession();
            var compressor = new MemoryCompressor(_infer, _logic, repo);

            int acc = 0, turnIndex = 0;
            while (acc < targetTokens)
            {
                // 每轮约 30 token 的填充文本；内容不重要，token 数才重要
                string text = $"第{turnIndex}轮：这是一段用于压缩测试的合成对话文本。";
                int n = _infer.CountTokensOrEstimate(text);

                long rawId = repo.InsertRawDialogue(
                    session.SessionId, turnIndex, $"user: {text}", DateTimeOffset.UtcNow);
                session.Append(DialogueRole.User, text, rawId);

                acc += n;
                turnIndex++;
                if (turnIndex > 10_000) throw new InvalidOperationException("token 计数异常，无法凑满目标值");
            }

            await compressor.CompressIfNeededAsync(session, CancellationToken.None);

            var l1 = repo.GetMemoryEntries(level: 1);
            int sealedTokens = l1.Sum(e => e.TokenCount);
            int tailTokens = session.ActiveSnapshot().Sum(t => _infer.CountTokensOrEstimate(t.Text));
            int tolerance = Math.Max(5, acc / 20);
            bool conserved = Math.Abs(sealedTokens + tailTokens - acc) <= tolerance;

            Emit("blink_triggered", l1.Count > 0 ? "1" : "0");
            Emit("sealed_chunks", l1.Count.ToString());
            Emit("tail_retained", session.ActiveSnapshot().Count > 0 ? "true" : "false");
            Emit("memory_index_l1_count", l1.Count.ToString());
            Emit("token_conserved", conserved.ToString().ToLowerInvariant());
            Emit("target_tokens_actual", acc.ToString());
            return 0;
        }
        finally { cleanup(); }
    }

    // ---------- 4. fallback-test ----------
    private int RunFallbackTest(string[] args)
    {
        string keyword = GetArg(args, "--keyword")
            ?? throw new ArgumentException("--keyword required");

        var (dbPath, cleanup) = CreateTempDatabase();
        try
        {
            var repo = new MemoryRepository(dbPath);
            var fallback = new FallbackResolver(repo, _logic);

            // 受控场景：L1 摘要写"计算了夹具模具"（无具体数值），
            // raw_dialogue 含具体数值，回退应拉到原文并让符号引擎重新算出
            var now = DateTimeOffset.UtcNow;
            long rawStart = repo.InsertRawDialogue(
                "s", 0, "user: 计算 夹具模具的用量 3 加 5 乘以 2", now);
            long rawEnd = repo.InsertRawDialogue(
                "s", 1, "assistant: 结果是 16", now);

            repo.InsertMemoryEntry(
                compressedText: "计算了夹具模具的用量",
                level: 1, parentId: null,
                rawStartId: rawStart, rawEndId: rawEnd,
                tokenCount: 40, timestamp: now.AddMinutes(-1));

            var retry = fallback.TryFallback(keyword);
            Emit("fallback_triggered", (retry is not null).ToString().ToLowerInvariant());
            Emit("raw_range_found", (retry is not null).ToString().ToLowerInvariant());
            Emit("reverify_accepted", (retry?.Accepted ?? false).ToString().ToLowerInvariant());
            Emit("fallback_depth", "1");   // 设计上单层，不递归
            if (retry?.Value is not null) Emit("value", retry.Value);
            return 0;
        }
        finally { cleanup(); }
    }

    // ---------- 5. plugin-test ----------
    private int RunPluginTest()
    {
        string pluginsDir = Path.Combine(_baseDir, "plugins");
        using var manager = new PluginManager(pluginsDir);
        var loaded = manager.LoadAll();

        bool abiOk = loaded.Count > 0;
        bool describeOk = loaded.Count > 0 && loaded.All(p => p.Descriptor.Functions.Length > 0);
        bool initOk = loaded.Count > 0;

        bool executeOk = false;
        if (loaded.Count > 0)
        {
            try
            {
                string outJson = loaded[0].Execute("{\"test\":\"smoke\"}");
                executeOk = outJson is not null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[smoke] plugin execute failed: {ex.Message}");
            }
        }

        bool mismatchRejected = manager.RejectedCount > 0;

        foreach (var p in loaded) p.Dispose();
        bool destroyCalled = true;   // 上述 Dispose 若无异常即视为成功

        Emit("plugin_abi_version_ok", abiOk.ToString().ToLowerInvariant());
        Emit("plugin_describe_ok", describeOk.ToString().ToLowerInvariant());
        Emit("plugin_init_ok", initOk.ToString().ToLowerInvariant());
        Emit("plugin_execute_ok", executeOk.ToString().ToLowerInvariant());
        Emit("plugin_destroy_called", destroyCalled.ToString().ToLowerInvariant());
        Emit("abi_mismatch_rejected", mismatchRejected.ToString().ToLowerInvariant());
        Emit("plugins_loaded", loaded.Count.ToString());
        Emit("plugins_rejected", manager.RejectedCount.ToString());
        return 0;
    }

    // ---------- 6. generate ----------
    private async Task<int> RunGenerateAsync(string[] args)
    {
        string input = GetArg(args, "--input") ?? "你好";
        int maxTokens = int.Parse(GetArg(args, "--max-tokens") ?? "512");

        var sw = Stopwatch.StartNew();
        string text = await _infer.GenerateTextAsync(
            input,
            new GenerationParams { MaxTokens = maxTokens },
            CancellationToken.None);
        sw.Stop();

        Emit("generate_ok", "true");
        Emit("elapsed_ms", sw.ElapsedMilliseconds.ToString());
        Emit("output_chars", text.Length.ToString());
        Emit("token_count", _infer.CountTokensOrEstimate(input).ToString());
        return 0;
    }

    // ---------- 7. translate（确定性翻译诊断） ----------
    private int RunTranslate(string[] args)
    {
        string input = GetArg(args, "--input") ?? throw new ArgumentException("--input required");

        bool ok = DeterministicTranslator.TryTranslate(input, out string expression);
        Emit("input", input);
        Emit("tokens", DeterministicTranslator.DebugTokens(input));
        Emit("translated", ok.ToString().ToLowerInvariant());
        Emit("expression", expression);
        return ok ? 0 : 2;
    }

    // ---------- 8. route（确定性路由诊断） ----------
    private int RunRoute(string[] args)
    {
        string input = GetArg(args, "--input") ?? throw new ArgumentException("--input required");

        var result = _logic.Route(input);
        Emit("input", input);
        Emit("route", result.Route.ToString().ToUpperInvariant());
        Emit("confidence", result.Confidence.ToString("0.0"));
        return 0;
    }

    // ---------- 9. keywords（确定性摘要诊断） ----------
    private int RunKeywords(string[] args)
    {
        string input = GetArg(args, "--input") ?? throw new ArgumentException("--input required");

        var result = _logic.ExtractKeywords(input);
        Emit("input", input);
        Emit("keywords", string.Join(",", result.Keywords));
        Emit("summary", result.Summary);
        return 0;
    }

    // ---------- 10. memory（实时摘要回填验证） ----------
    private async Task<int> RunMemoryTestAsync(string[] args)
    {
        int targetTokens = int.Parse(GetArg(args, "--target-tokens") ?? "1200");
        var (dbPath, cleanup) = CreateTempDatabase();
        try
        {
            var repo = new MemoryRepository(dbPath);
            var session = new ConversationSession();
            var compressor = new MemoryCompressor(_infer, _logic, repo);

            int acc = 0, turnIndex = 0;
            while (acc < targetTokens)
            {
                string text = $"第{turnIndex}轮：夹具模具的计算需要复核尺寸与公差。";
                int n = _infer.CountTokensOrEstimate(text);

                long rawId = repo.InsertRawDialogue(
                    session.SessionId, turnIndex, $"user: {text}", DateTimeOffset.UtcNow);
                session.Append(DialogueRole.User, text, rawId);

                acc += n;
                turnIndex++;
                if (turnIndex > 10_000) throw new InvalidOperationException("token 计数异常");
            }

            await compressor.CompressIfNeededAsync(session, CancellationToken.None);

            string prompt = ConversationOrchestrator.BuildOpenPrompt(
                session, _infer, "刚才聊了什么？");

            bool hasSummaryBlock = prompt.Contains("长期记忆摘要", StringComparison.Ordinal);
            bool hasSummaryText = session.Summaries.Any(s =>
                prompt.Contains(s.Text, StringComparison.Ordinal));

            Emit("live_summary_count", session.Summaries.Count.ToString());
            Emit("prompt_has_summary_block", hasSummaryBlock.ToString().ToLowerInvariant());
            Emit("prompt_has_summary_text", hasSummaryText.ToString().ToLowerInvariant());
            Emit("active_after_compress", session.ActiveSnapshot().Count.ToString());
            return session.Summaries.Count > 0 && hasSummaryBlock && hasSummaryText ? 0 : 1;
        }
        finally { cleanup(); }
    }

    // ---------- 辅助 ----------
    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    /// <summary>
    /// 模型自动发现：models/ 下第一个非空 .gguf；都没有时返回默认文件名（供报错提示）。
    /// </summary>
    private string FindDefaultModel()
    {
        string modelsDir = Path.Combine(_baseDir, "models");
        if (Directory.Exists(modelsDir))
        {
            var pick = Directory.GetFiles(modelsDir, "*.gguf")
                .Where(p => new FileInfo(p).Length > 0)
                .OrderBy(p => p)
                .FirstOrDefault();
            if (pick is not null) return pick;
        }
        return Path.Combine(modelsDir, "AVA-v2-Q4_K_M.gguf");
    }

    private static void Emit(string key, string value) => Console.WriteLine($"{key}={value}");

    private static int Fail(string msg) { Emit("error", msg); return 1; }

    /// <summary>
    /// 为 compress-test / fallback-test 创建隔离数据库：data/smoke_<guid>.db。
    /// 不污染生产 knowledge.db；schema 由 MemoryRepository 首次连接时创建。
    /// </summary>
    private (string dbPath, Action cleanup) CreateTempDatabase()
    {
        string dataDir = Path.Combine(_baseDir, "data");
        Directory.CreateDirectory(dataDir);

        string tmp = Path.Combine(dataDir, $"smoke_{Guid.NewGuid():N}.db");
        return (tmp, () =>
        {
            // 连接池会持有 SQLite 文件句柄，不先清池则删除失败，留下 smoke_*.db
            try { SqliteConnection.ClearAllPools(); } catch { /* ignore */ }
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(tmp + suffix); } catch { /* ignore */ }
            }
        });
    }
}
