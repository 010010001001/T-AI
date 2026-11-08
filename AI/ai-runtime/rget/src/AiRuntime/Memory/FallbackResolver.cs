public sealed class FallbackResolver
{
    private readonly MemoryRepository _repo;
    private readonly LogicEngine _logic;

    // 返回：重新裁决的结果；若无法回退则返回 null
    public VerifyResult? TryFallback(string failedKeyword)
    {
        // 步骤 1：用失败关键字查 FTS5，定位压缩条目
        var entries = _repo.SearchByKeyword(failedKeyword);
        if (entries.Count == 0) return null;

        // 步骤 2：取最近的条目（时间倒序第一），拉取它的原文范围
        var entry = entries.OrderByDescending(e => e.Timestamp).First();
        var originals = _repo.GetRawRange(entry.RawStartId, entry.RawEndId);
        if (originals.Count == 0) return null;

        // 步骤 3：将完整原文（非压缩摘要）拼接后重新交给符号引擎裁决
        // 注意：这里不再调用 LLM 生成候选，而是直接用原文作为候选来源
        var reconstituted = string.Join("\n", originals);
        return _logic.VerifyCandidate(reconstituted);
    }
}