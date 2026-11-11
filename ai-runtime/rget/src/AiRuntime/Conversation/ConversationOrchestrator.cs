// src/AiRuntime/Conversation/ConversationOrchestrator.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// 会话编排：路由 → 符号 / 工具 / 开放域三条路径，负责状态机、原文落库、
/// 压缩触发与回退链路。C# 层不介入逐 token 的推理循环，只以整批调用使用原生能力。
/// </summary>
public sealed class ConversationOrchestrator
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConversationSession _session;
    private readonly InferenceClient _infer;
    private readonly LogicEngine _logic;
    private readonly MemoryCompressor _compressor;
    private readonly MemoryRepository _repo;
    private readonly FallbackResolver _fallback;
    private readonly PluginManager _plugins;
    private readonly IReadOnlyList<IManagedTool> _managedTools;
    private readonly ToolPermissionManager _permissions;

    public ConversationOrchestrator(
        ConversationSession session,
        InferenceClient infer,
        LogicEngine logic,
        MemoryCompressor compressor,
        MemoryRepository repo,
        FallbackResolver fallback,
        PluginManager plugins,
        IReadOnlyList<IManagedTool> managedTools,
        ToolPermissionManager permissions)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _infer = infer ?? throw new ArgumentNullException(nameof(infer));
        _logic = logic ?? throw new ArgumentNullException(nameof(logic));
        _compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _managedTools = managedTools ?? throw new ArgumentNullException(nameof(managedTools));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    }

    /// <summary>冒烟测试与诊断用的 key=value 事件回调；正常运行时为 null。</summary>
    public Action<string>? OnTrace { get; set; }

    public async Task<string> HandleUserInputAsync(string userText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return "请输入问题。";

        userText = userText.Trim();
        _session.Transition(SessionState.Generating);

        try
        {
            var now = DateTimeOffset.UtcNow;
            long userRawId = _repo.InsertRawDialogue(
                _session.SessionId, _session.NextTurnIndex, $"user: {userText}", now);
            _session.Append(DialogueRole.User, userText, userRawId);

            // 每轮先眨眼：把已经凑满 1000 token 的历史压成摘要，控制上下文长度
            await _compressor.CompressIfNeededAsync(_session, ct).ConfigureAwait(false);

            var route = _logic.Route(userText);
            Trace($"route={RouteName(route.Route)}");

            string response = route.Route switch
            {
                RouteKind.Symbolic => await HandleSymbolicAsync(userText, ct).ConfigureAwait(false),
                RouteKind.Tool     => await HandleToolAsync(userText, ct).ConfigureAwait(false),
                _                  => await HandleOpenAsync(userText, ct).ConfigureAwait(false)
            };

            long assistantRawId = _repo.InsertRawDialogue(
                _session.SessionId, _session.NextTurnIndex, $"assistant: {response}", DateTimeOffset.UtcNow);
            _session.Append(DialogueRole.Assistant, response, assistantRawId);

            return response;
        }
        finally
        {
            if (_session.State != SessionState.Idle)
            {
                try { _session.Transition(SessionState.Idle); }
                catch (InvalidOperationException) { /* 状态已由子流程复位 */ }
            }
        }
    }

    // ==================================================================
    // 符号可解域：确定性翻译优先，LLM 只作候选提出者
    // ==================================================================

    private async Task<string> HandleSymbolicAsync(string question, CancellationToken ct)
    {
        string? candidate = null;

        // 1. 确定性翻译：不经过 LLM，优先保证精确度
        if (DeterministicTranslator.TryTranslate(question, out string deterministic))
        {
            candidate = deterministic;
        }
        else if (_infer.IsModelLoaded)
        {
            // 2. 精确度路径覆盖不了时才让 LLM 提出候选表达式
            candidate = await GenerateCandidateAsync(question, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(candidate))
            return "这个问题里没有可计算的数值或运算。请把算式写清楚，例如：3 加 5 乘以 2。";

        Trace($"llm_candidate_value={candidate}");

        // 3. 候选必须通过语法校验 + 符号运算才被采纳
        var verdict = _logic.VerifyCandidate(candidate);
        Trace($"verdict_accepted={verdict.Accepted.ToString().ToLowerInvariant()}");

        if (verdict.Accepted && verdict.Value is not null)
        {
            Trace($"value={verdict.Value}");
            Trace("source=sympy");
            return verdict.Value;
        }

        // 4. 自检失败：回退到原文重新裁决（只回退一层）
        _session.Transition(SessionState.Verifying);
        try
        {
            // 先按问题中提取到的关键字定位被压缩的原对话；关键字为空时退化为整句检索
            var keywords = _logic.ExtractKeywords(question).Keywords;
            var retry = keywords.Length > 0
                ? _fallback.TryFallback(keywords)
                : _fallback.TryFallback(question);

            if (retry is { Accepted: true, Value: not null })
            {
                Trace($"value={retry.Value}");
                Trace("source=sympy-fallback");
                return retry.Value;
            }
        }
        finally
        {
            _session.Transition(SessionState.Generating);
        }

        Trace("source=none");
        return "我无法可靠地计算这个问题。请把条件或数值写得更明确一些。";
    }

    /// <summary>LLM 仅负责自然语言 → 符号表达式的翻译，结果必须再过符号引擎校验。</summary>
    private async Task<string> GenerateCandidateAsync(string question, CancellationToken ct)
    {
        string prompt =
            "把下面的问题翻译成一个算术表达式。只输出表达式本身，不要解释、不要单位、不要等号。\n" +
            "可用字符：数字、+ - * / ( )。\n" +
            $"问题：{question}\n" +
            "表达式：";

        string raw = await _infer.GenerateTextAsync(
            prompt,
            new GenerationParams { MaxTokens = 64, Temperature = 0f, TopK = 1, TopP = 1f },
            ct).ConfigureAwait(false);

        return SanitizeExpression(raw);
    }

    private static string SanitizeExpression(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var sb = new StringBuilder();
        foreach (char c in raw.Trim())
        {
            if (char.IsDigit(c) || c is '+' or '-' or '*' or '/' or '(' or ')' or '.' or ' ')
                sb.Append(c);
            else if (c is '×' or 'x' or 'X') sb.Append('*');
            else if (c is '÷') sb.Append('/');
            else if (sb.Length > 0) break;   // 遇到第一段非表达式文本后停止
        }
        return sb.ToString().Trim();
    }

    // ==================================================================
    // 工具可解域：参数提取交给 LLM，执行由 C# 确定性完成
    // ==================================================================

    private async Task<string> HandleToolAsync(string question, CancellationToken ct)
    {
        if (_plugins.Plugins.Count == 0 && _managedTools.Count == 0)
            return "当前没有加载任何工具插件，无法执行这个请求。";

        // 明确的“执行命令 / 写入文件”指令优先走确定性解析，避免模型误选 echo
        if (TryDeterministicWorkspace(question, out var wsTool, out string wsFunction, out string wsArgs))
            return await ExecuteManagedAsync(wsTool!, wsFunction, wsArgs, question, ct).ConfigureAwait(false);

        ToolCall? call = await GenerateToolCallAsync(question, ct).ConfigureAwait(false);

        // echo 回显是本地 ABI 插件的稳定性测试，不依赖联网工具
        if (question.Contains("回显", StringComparison.Ordinal) &&
            _plugins.Plugins.FirstOrDefault(p =>
                p.Descriptor.Functions.Contains("echo", StringComparer.OrdinalIgnoreCase)) is { } echoPlugin)
        {
            string echoResult;
            try
            {
                echoResult = echoPlugin.Execute(JsonSerializer.Serialize(new { query = question }));
                Trace("plugin_execute_rc=0");
            }
            catch (Exception ex)
            {
                Trace("plugin_execute_rc=1");
                return $"工具执行失败：{ex.Message}";
            }
            Trace($"tool_call={echoPlugin.Name}.echo");
            Trace("executed_by=csharp");
            return await FormatToolResultAsync(question, echoResult, ct).ConfigureAwait(false);
        }

        // 模型没给出有效调用时，用确定性规则兜底（图片路径 / 语音关键词）
        if (call is null || string.IsNullOrWhiteSpace(call.Plugin) || string.IsNullOrWhiteSpace(call.Function))
        {
            if (TryDeterministicManaged(question, out var directTool, out string directFunction, out string directArgs))
                return await ExecuteManagedAsync(directTool!, directFunction, directArgs, question, ct).ConfigureAwait(false);
            return "我无法把这个问题解析成一次工具调用。请明确要执行的操作。";
        }

        Trace($"tool_call={call.Plugin}.{call.Function}");

        // 1) 托管工具（OCR / 语音识别 / 工作区）：执行前必须过权限守门
        var managed = FindManaged(call.Plugin, call.Function);
        if (managed is not null)
            return await ExecuteManagedAsync(managed, call.Function, call.ArgsToJson(), question, ct).ConfigureAwait(false);

        // 2) 原生 C ABI 插件
        var plugin = _plugins.Find(call.Plugin, call.Function);
        if (plugin is null)
        {
            if (TryDeterministicManaged(question, out var fallbackTool, out string fallbackFunction, out string fallbackArgs))
                return await ExecuteManagedAsync(fallbackTool!, fallbackFunction, fallbackArgs, question, ct).ConfigureAwait(false);

            return $"当前没有可用的工具来处理这个请求（请求: {call.Plugin}.{call.Function}）。";
        }

        string toolResult;
        try
        {
            toolResult = plugin.Execute(call.ArgsToJson());
            Trace("plugin_execute_rc=0");
        }
        catch (Exception ex)
        {
            Trace("plugin_execute_rc=1");
            return $"工具执行失败：{ex.Message}";
        }

        // 执行发生在 C# 层，LLM 只在参数提取和结果组织时参与
        Trace("executed_by=csharp");
        return await FormatToolResultAsync(question, toolResult, ct).ConfigureAwait(false);
    }

    /// <summary>明确的命令/写文件指令直接解析；其余交给 LLM 规划。</summary>
    private bool TryDeterministicWorkspace(
        string question, out IManagedTool? tool, out string function, out string argsJson)
    {
        tool = null;
        function = string.Empty;
        argsJson = string.Empty;

        string q = question.Trim();

        var exec = System.Text.RegularExpressions.Regex.Match(
            q, @"^(?:请)?(?:执行命令|运行命令|执行程序|运行程序)[:：\s]*(.+)$",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (exec.Success)
        {
            tool = _managedTools.FirstOrDefault(t => t.Name == "workspace_run");
            if (tool is null) return false;
            function = "run";
            argsJson = JsonSerializer.Serialize(new { command = exec.Groups[1].Value.Trim() });
            return true;
        }

        // "把 <path> 的内容写成 <content>" / "在 <path> 里写入 <content>"
        var write = System.Text.RegularExpressions.Regex.Match(
            q, @"^(?:请)?(?:把|将)\s*(.+?)\s*(?:的)?(?:内容)?(?:写成|改为|设置为)\s*(.+)$",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!write.Success)
            write = System.Text.RegularExpressions.Regex.Match(
                q, @"^(?:请)?在\s*(.+?)\s*(?:里|中)?\s*(?:写入|保存为)\s*(.+)$",
                System.Text.RegularExpressions.RegexOptions.Singleline);

        if (write.Success)
        {
            tool = _managedTools.FirstOrDefault(t => t.Name == "workspace_edit");
            if (tool is null) return false;
            function = "write";
            string path = write.Groups[1].Value.Trim().Trim('"', '\'');
            string content = write.Groups[2].Value.Trim().Trim('"', '\'');
            argsJson = JsonSerializer.Serialize(new { path, content });
            return true;
        }

        return false;
    }

    private async Task<string> ExecuteManagedAsync(
        IManagedTool tool, string function, string argsJson, string question, CancellationToken ct)
    {
        if (tool.RequiresPermission && !_permissions.EnsureAllowed(tool.Name))
            return "该能力需要用户授权，本次已取消。你可以说“保持图像权限”“保持语音权限”或“保持联网权限”后再试。";

        string result;
        try
        {
            result = tool.Execute(function, argsJson);
            Trace("plugin_execute_rc=0");
        }
        catch (Exception ex)
        {
            Trace("plugin_execute_rc=1");
            return $"工具执行失败：{ex.Message}";
        }
        finally
        {
            _permissions.Touch(tool.Name);
        }

        Trace($"tool_call={tool.Name}.{function}");
        Trace("executed_by=csharp");

        // 工作区工具直接返回确定性结果，避免模型改写执行结果
        if (tool.Name.StartsWith("workspace_", StringComparison.OrdinalIgnoreCase))
            return FormatWorkspaceResult(tool.Name, result);

        // 联网搜索即使模型不可用，也给出可读的搜索结果
        if (tool.Name.Equals("web_search", StringComparison.OrdinalIgnoreCase))
            return FormatWebSearchResult(result);

        return await FormatToolResultAsync(question, result, ct).ConfigureAwait(false);
    }

    private static string FormatWebSearchResult(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                return "搜索失败：" + (err.GetString() ?? "未知错误");

            if (!root.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return resultJson;

            var sb = new StringBuilder("搜索结果：");
            int index = 0;
            foreach (var item in results.EnumerateArray())
            {
                if (++index > 5) break;
                string title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                string url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                string snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";

                sb.Append($"\n{index}. {title}\n   {url}");
                if (snippet.Length > 0) sb.Append($"\n   {snippet}");
            }
            return sb.ToString();
        }
        catch (JsonException)
        {
            return resultJson;
        }
    }

    private static string FormatWorkspaceResult(string toolName, string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
                return "执行失败：" + (err.GetString() ?? "未知错误");

            if (toolName == "workspace_run")
            {
                string stdout = root.TryGetProperty("stdout", out var o) ? o.GetString() ?? "" : "";
                string stderr = root.TryGetProperty("stderr", out var e) ? e.GetString() ?? "" : "";
                int code = root.TryGetProperty("exit_code", out var c) ? c.GetInt32() : -1;
                var sb = new StringBuilder($"命令执行完成（退出码 {code}）");
                if (stdout.Length > 0) sb.Append("\n").Append(stdout.TrimEnd());
                if (stderr.Length > 0) sb.Append("\n[stderr] ").Append(stderr.TrimEnd());
                return sb.ToString();
            }

            if (toolName == "workspace_edit")
            {
                string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                long bytes = root.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0;
                return $"已写入 {path}（{bytes} 字节）";
            }

            if (toolName == "workspace_files")
            {
                if (root.TryGetProperty("content", out var content))
                {
                    string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                    return $"文件 {path}：\n{content.GetString()}";
                }

                if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                    var sb = new StringBuilder($"目录 {path}：");
                    foreach (var entry in entries.EnumerateArray())
                    {
                        string name = entry.GetProperty("name").GetString() ?? "";
                        string type = entry.GetProperty("type").GetString() ?? "";
                        long size = entry.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                        sb.Append($"\n- {name} ({type}{(type == "file" ? $", {size} B" : "")})");
                    }
                    return sb.ToString();
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 时原样返回
        }

        return resultJson;
    }

    /// <summary>LLM 参数提取失败时的确定性兜底：图片路径 / 语音关键词。</summary>
    private bool TryDeterministicManaged(
        string question, out IManagedTool? tool, out string function, out string argsJson)
    {
        tool = _managedTools.FirstOrDefault(t => t.Name == "image_ocr");
        function = "ocr";

        // 明确的联网搜索指令直接走 web_search（权限仍由 PermissionManager 守门）
        if (question.Contains("联网搜索") || question.Contains("网上搜索") ||
            question.Contains("网络搜索") || question.Contains("上网搜索"))
        {
            var web = _managedTools.FirstOrDefault(t => t.Name == "web_search");
            if (web is not null)
            {
                string query = question
                    .Replace("联网搜索", "", StringComparison.Ordinal)
                    .Replace("网上搜索", "", StringComparison.Ordinal)
                    .Replace("网络搜索", "", StringComparison.Ordinal)
                    .Replace("上网搜索", "", StringComparison.Ordinal)
                    .Trim().Trim('：', ':', ' ');
                if (query.Length == 0) query = question;
                tool = web;
                function = "search";
                argsJson = JsonSerializer.Serialize(new { query, count = 5 });
                return true;
            }
        }

        if (tool is not null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                question,
                @"([A-Za-z]:\\[^""'\r\n，。]+?\.(?:png|jpe?g|bmp|gif|webp))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                argsJson = JsonSerializer.Serialize(new { path = match.Groups[1].Value });
                return true;
            }
            tool = null;
        }

        if (question.Contains("语音") || question.Contains("听写") || question.Contains("录音") ||
            question.Contains("麦克风"))
        {
            tool = _managedTools.FirstOrDefault(t => t.Name == "speech_asr");
            if (tool is not null)
            {
                function = "recognize";
                argsJson = "{\"language\":\"zh-CN\",\"timeout_ms\":15000}";
                return true;
            }
        }

        argsJson = string.Empty;
        tool = null;
        function = string.Empty;
        return false;
    }

    private IManagedTool? FindManaged(string plugin, string function)
    {
        var exact = _managedTools.FirstOrDefault(t =>
            t.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase) &&
            t.Functions.Contains(function, StringComparer.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // LLM 偶尔把插件名写成动作名（如 search），按函数名兜底
        return _managedTools.FirstOrDefault(t =>
            t.Functions.Contains(function, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<ToolCall?> GenerateToolCallAsync(string question, CancellationToken ct)
    {
        if (!_infer.IsModelLoaded) return null;

        var catalogLines = new List<string>();
        catalogLines.AddRange(_plugins.Plugins.Select(p =>
            $"- {p.Descriptor.Name}: {string.Join(", ", p.Descriptor.Functions)}"));
        catalogLines.AddRange(_managedTools.Select(t =>
        {
            string line = $"- {t.Name}: {string.Join(", ", t.Functions)}" + t.Name switch
            {
                "image_ocr" => "，args 需要 {\"path\":\"图片文件路径\"}",
                "speech_asr" => "，args 可为 {\"language\":\"zh-CN\",\"timeout_ms\":10000}",
                "web_search" => "，search 的 args 为 {\"query\":\"...\",\"count\":5}（仅明确“联网搜索”时使用）",
                "workspace_files" => "，list/read 的 args 为 {\"path\":\".\"}",
                "workspace_edit" => "，write 的 args 为 {\"path\":\"...\",\"content\":\"...\"}（需要权限 2；写文件必须选它，不要用 echo）",
                "workspace_run" => "，run 的 args 为 {\"command\":\"...\",\"timeout_ms\":60000}（需要权限 3；执行命令/程序时必须选它）",
                _ => string.Empty
            };

            if (t.Name == "web_search" && !_permissions.IsEnabled("web_search"))
                line += "（当前未授权，禁止选择）";

            return line;
        }));
        catalogLines.Add("- 注意：echo 仅用于回显测试，不能写文件、不能执行命令。");
        string catalog = string.Join("\n", catalogLines);

        // 给一个严格使用 catalog 中名称的示例，避免模型把"插件名"写成用户的动词
        // 示例优先使用第一个原生插件（如 echo），避免把"搜索"类问题引导到 OCR
        string sampleName = _plugins.Plugins.Count > 0
            ? _plugins.Plugins[0].Descriptor.Name
            : _managedTools[0].Name;
        string sampleFn = _plugins.Plugins.Count > 0
            ? _plugins.Plugins[0].Descriptor.Functions[0]
            : _managedTools[0].Functions[0];
        string example = $"{{\"plugin\":\"{sampleName}\",\"function\":\"{sampleFn}\",\"args\":{{\"query\":\"...\"}}}}";

        string prompt =
            "根据可用工具，把用户问题转成一次工具调用。只输出 JSON，不要解释。\n" +
            "plugin 和 function 必须从下面的清单中原样选择，不能自造名称。\n" +
            "选择规则：普通搜索/查询/检索内容 → echo.search；只有明确说“联网搜索/网上搜索”才算 web_search.search；" +
            "回显测试 → echo.echo；" +
            "写文件 → workspace_edit.write；" +
            "执行命令/程序 → workspace_run.run；识别图片 → image_ocr.ocr；语音 → speech_asr.recognize。\n" +
            $"格式示例：{example}\n" +
            "可用工具：\n" + catalog + "\n" +
            $"用户：{question}\nJSON：";

        string raw = await _infer.GenerateTextAsync(
            prompt,
            new GenerationParams { MaxTokens = 128, Temperature = 0f, TopK = 1, TopP = 1f },
            ct).ConfigureAwait(false);

        Trace("tool_raw=" + raw.Replace("\r", " ").Replace("\n", " ")[..Math.Min(200, raw.Length)]);
        return NormalizeToolCall(ParseToolCall(raw));
    }

    /// <summary>
    /// 容忍模型常见的简化输出：只给 function（如 {"function":"echo"}）而不给 plugin。
    /// 按插件名、函数名反查归属，再补齐为可执行的 ToolCall。
    /// </summary>
    private ToolCall? NormalizeToolCall(ToolCall? call)
    {
        if (call is null) return null;

        string plugin = (call.Plugin ?? string.Empty).Trim();
        string function = (call.Function ?? string.Empty).Trim();
        if (plugin.Length > 0 && function.Length > 0) return call;

        if (plugin.Length > 0 && function.Length == 0)
        {
            var native = _plugins.Plugins.FirstOrDefault(p =>
                p.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase));
            if (native is not null)
                return new ToolCall(native.Name, native.Descriptor.Functions[0], call.Args);

            var managed = _managedTools.FirstOrDefault(t =>
                t.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase));
            if (managed is not null)
                return new ToolCall(managed.Name, managed.Functions[0], call.Args);
        }

        if (plugin.Length == 0 && function.Length > 0)
        {
            var nativeByName = _plugins.Plugins.FirstOrDefault(p =>
                p.Name.Equals(function, StringComparison.OrdinalIgnoreCase));
            if (nativeByName is not null)
                return new ToolCall(nativeByName.Name, nativeByName.Descriptor.Functions[0], call.Args);

            var managedByName = _managedTools.FirstOrDefault(t =>
                t.Name.Equals(function, StringComparison.OrdinalIgnoreCase));
            if (managedByName is not null)
                return new ToolCall(managedByName.Name, managedByName.Functions[0], call.Args);

            var managedByFn = _managedTools.FirstOrDefault(t =>
                t.Functions.Contains(function, StringComparer.OrdinalIgnoreCase));
            if (managedByFn is not null)
                return new ToolCall(managedByFn.Name, function, call.Args);

            var nativeByFn = _plugins.Plugins.FirstOrDefault(p =>
                p.Descriptor.Functions.Contains(function, StringComparer.OrdinalIgnoreCase));
            if (nativeByFn is not null)
                return new ToolCall(nativeByFn.Name, function, call.Args);
        }

        return call;
    }

    internal static ToolCall? ParseToolCall(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        string json = raw[start..(end + 1)];
        try
        {
            return JsonSerializer.Deserialize<ToolCall>(json, CaseInsensitiveJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> FormatToolResultAsync(string question, string toolResult, CancellationToken ct)
    {
        if (!_infer.IsModelLoaded) return CleanResponse(toolResult);

        string prompt =
            "根据工具返回的结果回答用户。只使用结果中的事实，不要编造。\n" +
            $"用户问题：{question}\n" +
            $"工具结果：{toolResult}\n" +
            "回答：";

        string formatted = await _infer.GenerateTextAsync(
            prompt,
            new GenerationParams { MaxTokens = 256, Temperature = 0.3f },
            ct).ConfigureAwait(false);

        return CleanResponse(formatted);
    }
    // ==================================================================
    // 开放域：LLM 自由生成，但必须通过一致性校验
    // ==================================================================

    private async Task<string> HandleOpenAsync(string question, CancellationToken ct)
    {
        if (!_infer.IsModelLoaded)
            return "开放域回答需要加载 LLM 模型。当前模型未加载，只能处理可确定性计算或工具调用的问题。";

        string prompt = BuildOpenPrompt(question);

        string text = await _infer.GenerateTextAsync(
            prompt,
            new GenerationParams { MaxTokens = 512, Temperature = 0.7f },
            ct).ConfigureAwait(false);

        text = CleanResponse(text);
        if (string.IsNullOrWhiteSpace(text))
            return "模型没有生成有效回答，请换个问法或补充上下文。";

        var check = _logic.CheckConsistency(text);
        if (!check.Consistent)
            return "我生成的内容与已知事实存在冲突，已撤回。请换个问法或补充上下文。";

        Trace("source=llm");
        return text;
    }

    /// <summary>
    /// 去掉 Qwen/AVA 的思考块与未闭合标签，避免把 &lt;think&gt; 泄漏给用户。
    /// </summary>
    private static string CleanResponse(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string cleaned = System.Text.RegularExpressions.Regex.Replace(
            text, "<think>.*?</think>", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = cleaned.Replace("<think>", string.Empty, StringComparison.OrdinalIgnoreCase)
                         .Replace("</think>", string.Empty, StringComparison.OrdinalIgnoreCase);

        // 模型偶发输出伪造的工具调用；截断到第一个 <tool_call> 之前
        int toolCall = cleaned.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
        if (toolCall >= 0) cleaned = cleaned[..toolCall];
        int endToken = cleaned.IndexOf("<|im_end|>", StringComparison.OrdinalIgnoreCase);
        if (endToken >= 0) cleaned = cleaned[..endToken];

        return cleaned.Trim();
    }

    private string BuildOpenPrompt(string question) => BuildOpenPrompt(_session, _infer, question);

    /// <summary>构造开放域提示词：长期摘要 + 最近对话，按 token 预算裁剪。</summary>
    internal static string BuildOpenPrompt(ConversationSession session, InferenceClient infer, string question)
    {
        var summaries = session.Summaries.ToList();
        var lines = session.ActivePromptLines().TakeLast(12).ToList();
        const int budget = 4096 - 512 - 64;   // 模型窗口 - 生成预留 - 模板余量

        static string FormatSummary(MemorySummary s) =>
            $"[L{s.Level} {s.At.ToLocalTime():yyyy-MM-dd}] {s.Text}";

        int Estimate() =>
            infer.CountTokensOrEstimate(question) +
            32 +
            summaries.Sum(s => infer.CountTokensOrEstimate(FormatSummary(s))) +
            lines.Sum(l => infer.CountTokensOrEstimate(l));

        // 超预算时先丢最旧的活跃轮次，再丢等级最低、时间最旧的摘要，保证最新问题与高层摘要优先保留
        while (Estimate() > budget && lines.Count > 0) lines.RemoveAt(0);
        while (Estimate() > budget && summaries.Count > 0)
        {
            var drop = summaries.OrderBy(s => s.Level).ThenBy(s => s.At).First();
            summaries.Remove(drop);
        }

        var sb = new StringBuilder();
        sb.AppendLine("你是本地部署的助手。回答要简洁；不确定时明确说不知道，不要编造。");

        if (summaries.Count > 0)
        {
            sb.AppendLine("长期记忆摘要（由旧对话压缩，可能失真；与用户新说法冲突时以新说法为准）：");
            foreach (var summary in summaries)
                sb.AppendLine(FormatSummary(summary));
        }

        sb.AppendLine("最近对话：");
        foreach (string line in lines)
            sb.AppendLine(line);

        sb.AppendLine($"用户：{question}");
        return sb.ToString();
    }

    // ==================================================================
    // 辅助
    // ==================================================================

    private static string RouteName(RouteKind route) => route switch
    {
        RouteKind.Symbolic => "SYMBOLIC",
        RouteKind.Tool     => "TOOL",
        _                  => "OPEN"
    };

    private void Trace(string evt) => OnTrace?.Invoke(evt);
}
