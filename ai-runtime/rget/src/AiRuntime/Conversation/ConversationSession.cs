// src/AiRuntime/Conversation/ConversationSession.cs
using System;
using System.Collections.Generic;
using System.Linq;

public enum SessionState
{
    Idle,
    Generating,
    Compressing,
    Verifying,
    Fallback
}

/// <summary>会话内的压缩摘要（与 memory_index 的 level/text/timestamp 对应）。</summary>
public sealed record MemorySummary(int Level, string Text, DateTimeOffset At);

/// <summary>
/// 单个会话的活跃上下文与状态机。
/// 活跃上下文只保存"尚未被眨眼压缩"的原文；压缩后摘要既写入记忆仓储，
/// 也实时回填到本会话的摘要列表，确保同一进程内后续对话仍能"记得"旧内容。
/// </summary>
public sealed class ConversationSession
{
    public string SessionId { get; private set; }
    public SessionState State { get; private set; } = SessionState.Idle;

    // 会话可见摘要：启动时从数据库恢复，运行中由 MemoryCompressor 实时追加
    private readonly List<MemorySummary> _summaries = new();
    private readonly List<DialogueTurn> _active = new();
    private readonly object _lock = new();
    private int _nextTurnIndex;

    public ConversationSession(string? sessionId = null)
    {
        SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? Guid.NewGuid().ToString("N")
            : sessionId;
    }

    /// <summary>下一个轮次的序号（从 0 开始），用于写入 raw_dialogue.turn_index。</summary>
    public int NextTurnIndex
    {
        get { lock (_lock) return _nextTurnIndex; }
    }

    public IReadOnlyList<DialogueTurn> ActiveSnapshot()
    {
        lock (_lock) return _active.ToArray();
    }

    public IReadOnlyList<MemorySummary> Summaries
    {
        get { lock (_lock) return _summaries.ToArray(); }
    }

    /// <summary>追加一轮对话并分配递增序号。rawId 必须是 raw_dialogue 中已经存在的行。</summary>
    public DialogueTurn Append(DialogueRole role, string text, long rawId)
    {
        lock (_lock)
        {
            var turn = new DialogueTurn(_nextTurnIndex, role, text, DateTimeOffset.UtcNow, rawId);
            _active.Add(turn);
            _nextTurnIndex++;
            return turn;
        }
    }

    /// <summary>
    /// 压缩完成后实时回填摘要。上升到更高层时，低层摘要会被高层摘要取代，
    /// 避免同一段历史在提示词中重复出现。
    /// </summary>
    public void AddSummary(MemorySummary summary)
    {
        if (summary is null || string.IsNullOrWhiteSpace(summary.Text)) return;

        lock (_lock)
        {
            if (summary.Level > 1)
                _summaries.RemoveAll(s => s.Level < summary.Level);

            bool duplicate = _summaries.Any(s =>
                s.Level == summary.Level && s.Text == summary.Text);
            if (!duplicate) _summaries.Add(summary);

            // 保守上限：提示词仍会按 token 预算裁剪，这里只防止无限增长
            if (_summaries.Count > 128)
            {
                var oldest = _summaries.OrderBy(s => s.At).First();
                _summaries.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// 跨天恢复：把数据库中的历史原文与摘要装回会话。
    /// turns 必须按时间升序；nextTurnIndex 取最后一轮的 turn_index + 1。
    /// </summary>
    public void Restore(
        string sessionId,
        IEnumerable<DialogueTurn> turns,
        IEnumerable<MemorySummary> summaries,
        int nextTurnIndex)
    {
        lock (_lock)
        {
            SessionId = sessionId;
            _active.Clear();
            _active.AddRange(turns);
            _summaries.Clear();
            _summaries.AddRange(summaries
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Text))
                .OrderByDescending(s => s.Level)
                .ThenByDescending(s => s.At));
            _nextTurnIndex = Math.Max(0, nextTurnIndex);
        }
    }

    /// <summary>新开启一轮对话：保留 SessionId 语义为“新会话”，清空活跃上下文与摘要。</summary>
    public void StartNew()
    {
        lock (_lock)
        {
            SessionId = Guid.NewGuid().ToString("N");
            _active.Clear();
            _summaries.Clear();
            _nextTurnIndex = 0;
        }
    }

    public void RemoveRange(int count)
    {
        lock (_lock)
        {
            if (count <= 0) return;
            if (count > _active.Count) count = _active.Count;
            _active.RemoveRange(0, count);
        }
    }

    /// <summary>压缩流程结束后，把剩余活跃上下文交给推理层时使用的文本行。</summary>
    public IReadOnlyList<string> ActivePromptLines()
    {
        lock (_lock) return _active.Select(t => t.ToPromptLine()).ToArray();
    }

    public void Transition(SessionState next)
    {
        if (!IsLegal(State, next))
            throw new InvalidOperationException($"illegal session transition: {State} -> {next}");
        State = next;
    }

    /// <summary>
    /// 状态转移表。压缩只是生成流程中的一个子步骤：
    /// Generating -> Compressing 再回到 Generating，不能让会话在压缩后落到 Idle，
    /// 否则后续的 Verifying / Fallback 转移全部非法。
    /// </summary>
    private static bool IsLegal(SessionState a, SessionState b) => (a, b) switch
    {
        (SessionState.Idle, SessionState.Generating)         => true,
        (SessionState.Idle, SessionState.Compressing)        => true,
        (SessionState.Generating, SessionState.Compressing)  => true,
        (SessionState.Compressing, SessionState.Generating)  => true,
        (SessionState.Compressing, SessionState.Idle)        => true,
        (SessionState.Generating, SessionState.Verifying)    => true,
        (SessionState.Generating, SessionState.Fallback)     => true,
        (SessionState.Generating, SessionState.Idle)         => true,
        (SessionState.Verifying, SessionState.Fallback)      => true,
        (SessionState.Verifying, SessionState.Generating)    => true,
        (SessionState.Verifying, SessionState.Idle)          => true,
        (SessionState.Fallback, SessionState.Verifying)      => true,
        (SessionState.Fallback, SessionState.Generating)     => true,
        (SessionState.Fallback, SessionState.Idle)           => true,
        _ => false
    };
}
