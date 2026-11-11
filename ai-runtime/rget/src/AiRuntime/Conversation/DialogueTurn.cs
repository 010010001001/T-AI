// src/AiRuntime/Conversation/DialogueTurn.cs
using System;

// 若项目采用命名空间，请用 namespace AiRuntime.Conversation; 包裹以下内容。
// 此处与阶段三骨架保持一致，不加命名空间。

/// <summary>
/// 对话轮次在系统内的唯一身份。Role 决定 prompt 拼接方式；
/// RawId 指向 raw_dialogue.id，是回退链路定位原文范围的唯一凭据。
/// </summary>
public enum DialogueRole
{
    System,
    User,
    Assistant
}

public static class DialogueRoleExtensions
{
    /// <summary>序列化到 SQLite / prompt 时使用的固定字符串，避免大小写漂移。</summary>
    public static string ToWireString(this DialogueRole role) => role switch
    {
        DialogueRole.System    => "system",
        DialogueRole.User      => "user",
        DialogueRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    public static DialogueRole ParseWireString(string s) => s switch
    {
        "system"    => DialogueRole.System,
        "user"      => DialogueRole.User,
        "assistant" => DialogueRole.Assistant,
        _ => throw new ArgumentException($"invalid role: {s}", nameof(s))
    };
}

/// <summary>
/// 单轮对话的不可变快照。构造后所有字段只读，可安全跨线程传递。
/// </summary>
public sealed record DialogueTurn
{
    /// <summary>会话内递增的轮次序号，从 0 开始。仅用于排序与调试，不参与回退定位。</summary>
    public int TurnIndex { get; }

    /// <summary>发言方。System 仅用于会话初始化的系统提示，通常不进入压缩流程。</summary>
    public DialogueRole Role { get; }

    /// <summary>完整原文。压缩摘要不存于此字段；此字段始终保存可直接送入推理层的文本。</summary>
    public string Text { get; }

    /// <summary>写入 raw_dialogue 时的 UTC 时间戳。用 DateTimeOffset 而非 DateTime，保留时区偏移。</summary>
    public DateTimeOffset At { get; }

    /// <summary>
    /// 对应 raw_dialogue.id。由 HandleUserInputAsync 在 InsertRawDialogue 之后回填。
    /// MemoryCompressor 用它构造 Chunk 的 FirstRawId / LastRawId；
    /// FallbackResolver 用它定位原文范围。必须 > 0。
    /// </summary>
    public long RawId { get; }

    public DialogueTurn(
        int turnIndex,
        DialogueRole role,
        string text,
        DateTimeOffset at,
        long rawId)
    {
        if (turnIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(turnIndex), "turnIndex must be >= 0");
        if (role is not (DialogueRole.System or DialogueRole.User or DialogueRole.Assistant))
            throw new ArgumentOutOfRangeException(nameof(role), "unknown role");
        if (text is null)
            throw new ArgumentNullException(nameof(text));
        if (rawId <= 0)
            throw new ArgumentOutOfRangeException(nameof(rawId), "rawId must be > 0");

        TurnIndex = turnIndex;
        Role = role;
        Text = text;
        At = at;
        RawId = rawId;
    }

    /// <summary>
    /// 拼接为 prompt 行的标准格式："role: text"。
    /// MemoryCompressor 与 HandleUserInputAsync 中凡需要把 turn 转为文本的地方，
    /// 统一调用此方法，避免各自手写字符串插值导致格式漂移。
    /// </summary>
    public string ToPromptLine() => $"{Role.ToWireString()}: {Text}";
}