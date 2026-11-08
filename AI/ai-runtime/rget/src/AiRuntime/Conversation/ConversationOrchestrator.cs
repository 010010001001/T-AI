public sealed class ConversationOrchestrator
{
    private readonly ConversationSession _session;
    private readonly InferenceClient _infer;
    private readonly LogicEngine _logic;
    private readonly MemoryCompressor _compressor;
    private readonly MemoryRepository _repo;
    private readonly FallbackResolver _fallback;

    public async Task<string> HandleUserInputAsync(string userText, CancellationToken ct)
    {
        // 1. 记录原始对话（先写，保证回退可查）
        long rawId = NextRawId();
        _repo.InsertRawDialogue(rawId, _session.SessionId, _session.TurnIndex,
            $"user: {userText}", DateTimeOffset.UtcNow);
        _session.AppendTurn(new DialogueTurn(_session.TurnIndex, "user", userText, DateTimeOffset.UtcNow));

        // 2. 压缩检查（在生成之前，保证活跃上下文不超预算）
        await _compressor.CompressIfNeededAsync(_session, ct);

        // 3. 路由
        var route = _logic.Route(userText);
        string response = route.Route switch
        {
            RouteKind.Symbolic => await HandleSymbolicAsync(userText, ct),
            RouteKind.Tool     => await private async Task<string> HandleToolAsync(string question, CancellationToken ct)
{
    // 步骤 1：LLM 仅作为"参数提取器"
    // prompt 要求 LLM 输出 JSON：{"plugin":"search","args":{"query":"家具行业"}}
    var candidateJson = await GenerateToolCallAsync(question, ct);

    // 步骤 2：语法校验（JSON 合法性 + 插件名存在 + 参数模式匹配）
    ToolCall? call;
    try { call = JsonSerializer.Deserialize<ToolCall>(candidateJson); }
    catch { return "我无法将你的请求解析为工具调用。请明确你要执行的操作。"; }

    var plugin = _plugins.FirstOrDefault(p =>
        p.Descriptor.Name == call.Plugin &&
        p.Descriptor.Functions.Contains(call.Function));
    if (plugin is null)
        return $"当前没有可用的工具来处理这个请求（请求: {call.Plugin}.{call.Function}）。";

    // 步骤 3：由 C# 确定性代码执行工具调用
    // 注意：LLM 不执行工具，只提取参数；执行权在 C# 层
    string toolResult;
    try { toolResult = plugin.Execute(candidateJson); }
    catch (Exception ex) { return $"工具执行失败：{ex.Message}"; }

    // 步骤 4：LLM 组织结果（可选；若结果已是自然语言，直接返回）
    return await FormatToolResultAsync(question, toolResult, ct);
}

public sealed record ToolCall(string Plugin, string Function, Dictionary<string, string> Args);(userText, ct),   // 阶段四
            _                  => await HandleOpenAsync(userText, ct)
        };

        // 4. 记录助手回复
        _repo.InsertRawDialogue(NextRawId(), _session.SessionId, _session.TurnIndex,
            $"assistant: {response}", DateTimeOffset.UtcNow);
        _session.AppendTurn(new DialogueTurn(_session.TurnIndex, "assistant", response, DateTimeOffset.UtcNow));
        return response;
    }

    private async Task<string> HandleSymbolicAsync(string question, CancellationToken ct)
    {
        // a. LLM 仅作为"自然语言 -> 符号表达式"翻译器
        var candidate = await GenerateCandidateAsync(question, ct);

        // b. 符号引擎裁决（LLM 的数值被丢弃）
        var verdict = _logic.VerifyCandidate(candidate);
        if (verdict.Accepted) return verdict.Value!;

        // c. 回退：用失败关键字定位原文重新裁决
        _session.Transition(SessionState.Verifying);
        var retry = _fallback.TryFallback(verdict.Reason ?? "unknown");
        _session.Transition(SessionState.Idle);
        if (retry is { Accepted: true }) return retry.Value!;

        // d. 保守处理：澄清
        return "我无法可靠地计算这个问题。你能把条件或数值写得更明确吗？";
    }

    private async Task<string> HandleOpenAsync(string question, CancellationToken ct)
    {
        // 开放域：LLM 正常生成，但输出需经形式化一致性校验
        var text = await GenerateAsync(question, ct);
        var check = _logic.CheckConsistency(text);
        if (!check.Consistent)
            return "我生成的内容与已知事实存在冲突，已撤回。请换个问法或提供更多上下文。";
        return text;
    }
}