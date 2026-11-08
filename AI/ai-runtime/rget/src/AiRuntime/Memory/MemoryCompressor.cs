public sealed class MemoryCompressor
{
    private readonly InferenceClient _infer;
    private readonly LogicEngine _logic;
    private readonly MemoryRepository _repo;

    // 可调参数（构建时固定，运行时只读）
    private const int BlinkTokenUnit      = 1000;   // 每 1000 token 眨眼一次
    private const int L1ToL2Threshold     = 3000;   // 日级条目累计超过此值触发月压缩
    private const int L2ToL3Threshold     = 6000;   // 月级条目累计超过此值触发年压缩
    private const int L3ToL4Threshold     = 12000;  // 年级条目累计超过此值触发多年压缩

    public async Task CompressIfNeededAsync(ConversationSession session, CancellationToken ct)
    {
        session.Transition(SessionState.Compressing);
        try
        {
            var active = session.ActiveSnapshot();
            if (active.Count == 0) return;

            // --- 步骤 1：以 1000 token 为单位切分活跃上下文 ---
            var chunks = ChunkByTokens(active, BlinkTokenUnit);
            if (chunks.Count == 0) return;

            // 只有"满 1000 token 的完整块"才被压缩；尾块保留，等下一轮凑满
            var sealedChunks = chunks.Where(c => c.TokenCount >= BlinkTokenUnit).ToList();
            if (sealedChunks.Count == 0) return;

            // --- 步骤 2：逐块确定性提取关键字，写入 L1 ---
            foreach (var chunk in sealedChunks)
            {
                var text = string.Join("\n", chunk.Turns.Select(t => $"{t.Role}: {t.Text}"));
                var kw = _logic.ExtractKeywords(text);           // 不调用 LLM
                _repo.InsertMemoryEntry(
                    compressedText: kw.Summary,
                    level: 1,
                    parentId: null,
                    rawStartId: chunk.FirstRawId,
                    rawEndId: chunk.LastRawId,
                    tokenCount: chunk.TokenCount,
                    timestamp: chunk.Turns[^1].At);
            }

            // 移除已压缩的块，保留尾块
            session.RemoveRange(sealedChunks.Sum(c => c.Turns.Count));

            // --- 步骤 3：检查上级是否超阈值，逐级上卷 ---
            await CascadeAsync(1, L1ToL2Threshold, ct);   // L1 -> L2
            await CascadeAsync(2, L2ToL3Threshold, ct);   // L2 -> L3
            await CascadeAsync(3, L3ToL4Threshold, ct);   // L3 -> L4（3/5/10 年）

            // --- 步骤 4：若压缩后活跃上下文仍超预算，循环再压缩 ---
            // （对应"若长度依旧会造成丢失风险，即再次进行循环提取关键字"）
            int remaining = session.ActiveSnapshot()
                .Sum(t => _infer.CountTokens(t.Text));
            if (remaining > BlinkTokenUnit * 2)
            {
                // 递归一次；若仍超限，说明单轮对话本身超长，交由调用方拆轮
                await CompressIfNeededAsync(session, ct);
            }
        }
        finally { session.Transition(SessionState.Idle); }
    }

    private async Task CascadeAsync(int fromLevel, int threshold, CancellationToken ct)
    {
        // 取该级所有条目，累计 token 数
        var entries = _repo.GetMemoryEntries(level: fromLevel);
        int total = entries.Sum(e => e.TokenCount);
        if (total <= threshold) return;

        // 将全部 fromLevel 条目合并为一条 fromLevel+1 条目
        var mergedText = string.Join("；", entries.Select(e => e.CompressedText));
        var kw = _logic.ExtractKeywords(mergedText);   // 再次确定性提取，不调用 LLM

        _repo.InsertMemoryEntry(
            compressedText: kw.Summary,
            level: fromLevel + 1,
            parentId: null,
            rawStartId: entries.Min(e => e.RawStartId),
            rawEndId: entries.Max(e => e.RawEndId),
            tokenCount: total,
            timestamp: entries.Max(e => e.Timestamp));

        _repo.DeleteMemoryEntries(entries.Select(e => e.Id));
    }

    private sealed record Chunk(List<DialogueTurn> Turns, int TokenCount, long FirstRawId, long LastRawId);

    private List<Chunk> ChunkByTokens(IReadOnlyList<DialogueTurn> turns, int unit)
    {
        // 按 token 累积切块；块边界不跨越 turn（保证 raw_start/end 指向完整对话轮）
        var result = new List<Chunk>();
        var current = new List<DialogueTurn>();
        int acc = 0;
        foreach (var t in turns)
        {
            int n = _infer.CountTokens(t.Text);
            if (acc + n > unit && current.Count > 0)
            {
                result.Add(new Chunk(current, acc, current[0].RawId, current[^1].RawId));
                current = new(); acc = 0;
            }
            current.Add(t); acc += n;
        }
        if (current.Count > 0)
            result.Add(new Chunk(current, acc, current[0].RawId, current[^1].RawId));
        return result;
    }
}