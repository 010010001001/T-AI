using System.Runtime.InteropServices;
using System.Text.Json;

internal static class LogicNative
{
    private const string Dll = "logic_engine";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_route([MarshalAs(UnmanagedType.LPUTF8Str)] string q, out IntPtr json);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_verify_candidate([MarshalAs(UnmanagedType.LPUTF8Str)] string c, out IntPtr json);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_extract_keywords([MarshalAs(UnmanagedType.LPUTF8Str)] string t, out IntPtr json);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_consistency_check([MarshalAs(UnmanagedType.LPUTF8Str)] string t, out IntPtr json);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ai_free_string(IntPtr s);
}

public enum RouteKind { Symbolic, Tool, Open }

public sealed record RouteResult(RouteKind Route, double Confidence);
public sealed record VerifyResult(bool Accepted, string? Value, string? Reason, string? Detail);
public sealed record KeywordResult(string[] Keywords, string Summary);
public sealed record ConsistencyResult(bool Consistent, string[] Conflicts);

public sealed class LogicEngine
{
    private static T ParseAndFree<T>(IntPtr json)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8(json) ?? "{}";
            return JsonSerializer.Deserialize<T>(text)!;
        }
        finally { LogicNative.ai_free_string(json); }
    }

    public RouteResult Route(string question)
    {
        LogicNative.ai_route(question, out var p);
        using var doc = JsonDocument.Parse(Marshal.PtrToStringUTF8(p) ?? "{}");
        var route = doc.RootElement.GetProperty("route").GetString() switch
        {
            "SYMBOLIC" => RouteKind.Symbolic,
            "TOOL"     => RouteKind.Tool,
            _          => RouteKind.Open
        };
        var conf = doc.RootElement.GetProperty("confidence").GetDouble();
        LogicNative.ai_free_string(p);
        return new RouteResult(route, conf);
    }

    public VerifyResult VerifyCandidate(string candidate)
    {
        LogicNative.ai_verify_candidate(candidate, out var p);
        return ParseAndFree<VerifyResult>(p);
    }

    public KeywordResult ExtractKeywords(string text)
    {
        LogicNative.ai_extract_keywords(text, out var p);
        return ParseAndFree<KeywordResult>(p);
    }

    public ConsistencyResult CheckConsistency(string text)
    {
        LogicNative.ai_consistency_check(text, out var p);
        return ParseAndFree<ConsistencyResult>(p);
    }
}