// src/AiRuntime/Logic/LogicEngine.cs
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 意图路由的三个域。与原生逻辑引擎输出的字符串一一对应：
///   SYMBOLIC - 符号可解域（数学、逻辑、形式推理）
///   TOOL     - 工具可解域（检索、API、本地文件）
///   OPEN     - 开放域（主观评价、创意、模糊语义）
/// </summary>
public enum RouteKind { Symbolic, Tool, Open }

public sealed record RouteResult(RouteKind Route, double Confidence);

public sealed record VerifyResult(bool Accepted, string? Value, string? Reason, string? Detail);

public sealed record SymbolResult(string Status, string? Value, string? Proof, string? Error);

public sealed record KeywordResult(string[] Keywords, string Summary);

public sealed record ConsistencyResult(bool Consistent, string[] Conflicts);

/// <summary>
/// 逻辑引擎的托管门面：负责调用原生 logic_engine.dll、校验返回码、解析 JSON，
/// 并在所有路径上释放原生分配的字符串。
/// </summary>
public sealed class LogicEngine
{
    // 原生层输出的是 snake_case 键；必须显式开启大小写不敏感，
    // 否则 "accepted"/"keywords"/"consistent" 不会被反序列化到对应属性。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // 原生层输出 "SYMBOLIC"/"TOOL"/"OPEN" 字符串；默认枚举器只接受数字
        Converters = { new JsonStringEnumConverter() }
    };

    private static T ParseAndFree<T>(IntPtr json, int rc, string operation)
    {
        if (rc != 0)
            throw new InvalidOperationException($"{operation} failed: native rc={rc}");

        if (json == IntPtr.Zero)
            throw new InvalidOperationException($"{operation} returned null json");

        try
        {
            string text = Marshal.PtrToStringUTF8(json)
                ?? throw new InvalidOperationException($"{operation} returned invalid utf8");
            return JsonSerializer.Deserialize<T>(text, JsonOptions)
                ?? throw new InvalidOperationException($"{operation} returned empty json");
        }
        finally
        {
            LogicNative.FreeString(json);
        }
    }

    /// <summary>确定性规则路由，不经过 LLM。</summary>
    public RouteResult Route(string question)
    {
        ArgumentException.ThrowIfNullOrEmpty(question);

        int rc = LogicNative.ai_route(question, out IntPtr json);
        return ParseAndFree<RouteResult>(json, rc, "ai_route");
    }

    /// <summary>
    /// LLM 候选表达式的符号引擎裁决：语法校验 + 符号运算。
    /// 只有 accepted 为 true 时 Value 才来自符号引擎，可直接作为最终答案。
    /// </summary>
    public VerifyResult VerifyCandidate(string candidate)
    {
        ArgumentException.ThrowIfNullOrEmpty(candidate);

        int rc = LogicNative.ai_verify_candidate(candidate, out IntPtr json);
        return ParseAndFree<VerifyResult>(json, rc, "ai_verify_candidate");
    }

    /// <summary>直接执行一条已经过语法校验的符号表达式（确定性路径）。</summary>
    public SymbolResult ExecuteSymbol(string expression)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression);

        int rc = LogicNative.ai_symbol_execute(expression, out IntPtr json);
        return ParseAndFree<SymbolResult>(json, rc, "ai_symbol_execute");
    }

    /// <summary>确定性关键字提取（不调用 LLM），用于"眨眼"压缩与回退检索。</summary>
    public KeywordResult ExtractKeywords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int rc = LogicNative.ai_extract_keywords(text, out IntPtr json);
        return ParseAndFree<KeywordResult>(json, rc, "ai_extract_keywords");
    }

    /// <summary>开放域文本的形式化一致性校验（矛盾检测）。</summary>
    public ConsistencyResult CheckConsistency(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int rc = LogicNative.ai_consistency_check(text, out IntPtr json);
        return ParseAndFree<ConsistencyResult>(json, rc, "ai_consistency_check");
    }
}
