public enum SessionState { Idle, Generating, Compressing, Verifying, Fallback }

public sealed class ConversationSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public SessionState State { get; private set; } = SessionState.Idle;
    public int TurnIndex { get; private set; }

    // 当前"活跃上下文"：尚未被压缩、仍需参与推理的原文
    private readonly List<DialogueTurn> _active = new();
    private readonly object _lock = new();

    public IReadOnlyList<DialogueTurn> ActiveSnapshot()
    {
        lock (_lock) return _active.ToArray();
    }

    public void AppendTurn(DialogueTurn turn)
    {
        lock (_lock) { _active.Add(turn); TurnIndex++; }
    }

    public void RemoveRange(int count)
    {
        lock (_lock) _active.RemoveRange(0, count);
    }

    public void Transition(SessionState next)
    {
        // 合法转移表：Idle->Generating, Generating->Compressing|Verifying|Idle,
        // Compressing->Idle, Verifying->Fallback|Idle, Fallback->Verifying|Idle
        if (!IsLegal(State, next)) throw new InvalidOperationException($"{State}->{next}");
        State = next;
    }

    private static bool IsLegal(SessionState a, SessionState b) => (a, b) switch
    {
        (SessionState.Idle, SessionState.Generating)        => true,
        (SessionState.Generating, SessionState.Compressing) => true,
        (SessionState.Generating, SessionState.Verifying)   => true,
        (SessionState.Generating, SessionState.Idle)        => true,
        (SessionState.Compressing, SessionState.Idle)       => true,
        (SessionState.Verifying, SessionState.Fallback)     => true,
        (SessionState.Verifying, SessionState.Idle)         => true,
        (SessionState.Fallback, SessionState.Verifying)     => true,
        (SessionState.Fallback, SessionState.Idle)          => true,
        _ => false
    };
}

public sealed record DialogueTurn(int TurnIndex, string Role, string Text, DateTimeOffset At);