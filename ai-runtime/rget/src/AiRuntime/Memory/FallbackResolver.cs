// src/AiRuntime/Memory/FallbackResolver.cs
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 回退链路：当符号裁决失败时，按失败关键字在知识库中定位已压缩条目，
/// 拉取该条目覆盖的完整原文（不是摘要），重新翻译为候选表达式并再次交给
/// 符号引擎裁决。
/// </summary>
/// <remarks>
/// 设计约束：
///   - 回退只发生一层，不做递归，避免在"错误被谁判定"上无限循环；
///   - 重新翻译优先走确定性翻译器；确定性不可用时才允许调用方另行接入 LLM；
///   - 重新裁决通过时，最终值来自符号引擎，不采信任何 LLM 数值。
/// </remarks>
public sealed class FallbackResolver
{
    private readonly MemoryRepository _repo;
    private readonly LogicEngine _logic;

    public FallbackResolver(MemoryRepository repo, LogicEngine logic)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _logic = logic ?? throw new ArgumentNullException(nameof(logic));
    }

    /// <summary>
    /// 返回重新裁决的结果；若无法定位原文或原文无法翻译成可验证表达式，返回 null。
    /// </summary>
    public VerifyResult? TryFallback(string failedKeyword)
    {
        if (string.IsNullOrWhiteSpace(failedKeyword)) return null;

        var entries = _repo.SearchByKeyword(failedKeyword);
        if (entries.Count == 0) return null;

        foreach (var entry in entries.OrderByDescending(e => e.Timestamp))
        {
            var originals = _repo.GetRawRange(entry.RawStartId, entry.RawEndId);
            if (originals.Count == 0) continue;

            if (!DeterministicTranslator.TryTranslateDialogue(originals, out string expression))
                continue;   // 这段原文里没有可确定性计算的内容，试下一条

            var verdict = _logic.VerifyCandidate(expression);
            if (verdict.Accepted) return verdict;
        }

        return null;
    }

    /// <summary>
    /// 按关键字列表依次回退，命中即返回；全部未命中返回 null。
    /// </summary>
    public VerifyResult? TryFallback(IEnumerable<string> keywords)
    {
        if (keywords is null) return null;

        foreach (string keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword)) continue;
            var result = TryFallback(keyword);
            if (result is not null) return result;
        }
        return null;
    }
}
