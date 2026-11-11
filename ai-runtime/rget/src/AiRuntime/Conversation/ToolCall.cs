// src/AiRuntime/Conversation/ToolCall.cs
using System.Text.Json;

/// <summary>
/// LLM 给出的候选工具调用。Args 保留为 JSON 元素，
/// 以便把参数原样交给插件（插件契约是 JSON in / JSON out）。
/// </summary>
public sealed record ToolCall(string Plugin, string Function, JsonElement Args)
{
    /// <summary>把 Args 序列化为插件输入 JSON；缺省时给出空对象。</summary>
    public string ArgsToJson() =>
        Args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : Args.GetRawText();
}
