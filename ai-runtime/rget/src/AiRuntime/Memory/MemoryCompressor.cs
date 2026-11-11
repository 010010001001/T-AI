// src/AiRuntime/Memory/MemoryCompressor.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// "眨眼"记忆压缩：以 1000 token 为单位截取活跃上下文，确定性提取关键字写入 L1，
/// 再按 L1→L2（月）→L3（年）→L4（3/5/10 年）逐级上卷。
/// <b>不调用 LLM</b>：关键字提取走原生逻辑引擎的确定性规则。
/// </summary>
/// <remarks>
/// 流程（对应需求）：
///   1. 每累计 1000 token 执行一次"眨眼"：把该段原文替换为摘要；
///   2. 摘要不足 1000 token 的尾块保留在活跃上下文，等下一轮凑满；
///   3. 压缩后仍超预算时，循环继续提取关键字；
///   4. 日/月/年/多年一级级上卷，超出阈值就合并成上一级摘要。
///
/// 状态机：压缩是生成流程的子步骤，进入 Generating -> Compressing，
/// 结束时必须回到 Generating（而不是 Idle），否则后续 Verifying 转移非法。
/// </remarks>
public sealed class MemoryCompressor
{
    private readonly InferenceClient _infer;
    private readonly LogicEngine _logic;
    private readonly MemoryRepository _repo;

    // 可调参数（构建时固定，运行时只读）
    public const int BlinkTokenUnit   = 1000;  // 每 1000 token 眨眼一次
    public const int ActiveBudget     = 2000;  // 活跃上下文预算：超过则继续循环提取
    public const int L1ToL2Threshold  = 3000;  // 日级条目累计超过此值触发月压缩
    public const int L2ToL3Threshold  = 6000;  // 月级条目累计超过此值触发年压缩
    public const int L3ToL4Threshold  = 12000; // 年级条目累计超过此值触发多年压缩

    private const int MaxCompressionRounds = 8;

    public MemoryCompressor(InferenceClient infer, LogicEngine logic, MemoryRepository repo)
    {
        _infer = infer ?? throw new ArgumentNullException(nameof(infer));
        _logic = logic ?? throw new ArgumentNullException(nameof(logic));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public Task CompressIfNeededAsync(ConversationSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var previous = session.State;
        session.Transition(SessionState.Compressing);
        try
        {
            // --- 步骤 1-2：循环眨眼，直到没有新的满块可压缩 ---
            for (int round = 0; round < MaxCompressionRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                var active = session.ActiveSnapshot();
                if (active.Count == 0) break;

                var chunks = ChunkByTokens(active, BlinkTokenUnit);
                var sealedChunks = TakeSealedPrefix(chunks);
                if (sealedChunks.Count == 0) break;   // 只剩尾块，等下一轮凑满

                foreach (var chunk in sealedChunks)
                {
                    string text = string.Join("\n", chunk.Turns.Select(t => t.ToPromptLine()));
                    var kw = _logic.ExtractKeywords(text);   // 确定性提取，不调用 LLM
                    string summary = kw.Summary.Length > 0 ? kw.Summary : text;

                    _repo.InsertMemoryEntry(
                        compressedText: summary,
                        level: 1,
                        parentId: null,
                        rawStartId: chunk.FirstRawId,
                        rawEndId: chunk.LastRawId,
                        tokenCount: chunk.TokenCount,
                        timestamp: chunk.Turns[^1].At);

                    // 实时回填到会话：同一进程内后续对话仍能看到刚压缩的内容
                    session.AddSummary(new MemorySummary(1, summary, chunk.Turns[^1].At));
                }

                session.RemoveRange(sealedChunks.Sum(c => c.Turns.Count));

                int remaining = session.ActiveSnapshot().Sum(t => _infer.CountTokensOrEstimate(t.Text));
                if (remaining <= ActiveBudget) break;
            }

            // --- 步骤 3：按阈值逐级上卷 ---
            Cascade(session, 1, L1ToL2Threshold);
            Cascade(session, 2, L2ToL3Threshold);
            Cascade(session, 3, L3ToL4Threshold);
        }
        finally
        {
            // 回到进入压缩前的状态：编排路径下是 Generating，独立调用时是 Idle
            session.Transition(previous);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 取出可以压缩的完整块：从前往后，遇到第一个不足 1000 token 的块就停止。
    /// 尾块必须保留在活跃上下文中，否则"等下一轮凑满"的设计失效。
    /// </summary>
    private static List<Chunk> TakeSealedPrefix(List<Chunk> chunks)
    {
        var sealedChunks = new List<Chunk>();
        foreach (var chunk in chunks)
        {
            if (chunk.TokenCount < BlinkTokenUnit) break;
            sealedChunks.Add(chunk);
        }
        return sealedChunks;
    }

    private void Cascade(ConversationSession session, int fromLevel, int threshold)
    {
        var entries = _repo.GetMemoryEntries(level: fromLevel);
        if (entries.Count == 0) return;

        int total = entries.Sum(e => e.TokenCount);
        if (total <= threshold) return;

        string mergedText = string.Join("；", entries.Select(e => e.CompressedText));
        var kw = _logic.ExtractKeywords(mergedText);
        string summary = kw.Summary.Length > 0 ? kw.Summary : mergedText;

        long rawStart = entries.Min(e => e.RawStartId);
        long rawEnd = entries.Max(e => e.RawEndId);
        var timestamp = entries.Max(e => e.Timestamp);

        _repo.InsertMemoryEntry(
            compressedText: summary,
            level: fromLevel + 1,
            parentId: null,
            rawStartId: rawStart,
            rawEndId: rawEnd,
            tokenCount: total,
            timestamp: timestamp);

        // 高层摘要取代低层：AddSummary 会移除等级更低的会话摘要
        session.AddSummary(new MemorySummary(fromLevel + 1, summary, timestamp));

        // 子条目由新摘要代表；原文仍保留在 raw_dialogue，回退链路按 id 范围取回
        _repo.DeleteMemoryEntries(entries.Select(e => e.Id));
    }

    private sealed record Chunk(
        List<DialogueTurn> Turns,
        int TokenCount,
        long FirstRawId,
        long LastRawId);

    private List<Chunk> ChunkByTokens(IReadOnlyList<DialogueTurn> turns, int unit)
    {
        var result = new List<Chunk>();
        var current = new List<DialogueTurn>();
        int acc = 0;

        foreach (var turn in turns)
        {
            int n = Math.Max(1, _infer.CountTokensOrEstimate(turn.Text));

            current.Add(turn);
            acc += n;

            // 攒满“眨眼单位”才切块；块允许略超 1000，剩余部分作为下一个块。
            // 若在超限前切分，块永远达不到 unit，眨眼就永远不会触发。
            if (acc >= unit)
            {
                result.Add(new Chunk(current, acc, current[0].RawId, current[^1].RawId));
                current = new List<DialogueTurn>();
                acc = 0;
            }
        }

        if (current.Count > 0)
            result.Add(new Chunk(current, acc, current[0].RawId, current[^1].RawId));

        return result;
    }
}
