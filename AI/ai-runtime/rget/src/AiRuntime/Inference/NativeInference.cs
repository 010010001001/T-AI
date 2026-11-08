using System.Runtime.InteropServices;
internal static extern int ai_count_tokens(string text)

internal static class Native
{
    private const string Dll = "ai_inference";   // 解析为 native/ai_inference.dll

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TokenCallback(IntPtr tokenUtf8, IntPtr userData);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_backend_init([MarshalAs(UnmanagedType.LPUTF8Str)] string backendsDir);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ai_generate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string paramsJson,
        TokenCallback cb, IntPtr userData, out IntPtr errorJson);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ai_free_string(IntPtr s);
}

public sealed class InferenceClient
{
    private readonly Action<string> _onToken;

    public InferenceClient(Action<string> onToken) => _onToken = onToken;

    public Task GenerateAsync(string prompt, string paramsJson, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            // 委托必须保持强引用直到调用返回，否则 GC 可能回收导致回调崩溃
            Native.TokenCallback cb = (tok, _) =>
            {
                if (ct.IsCancellationRequested) return;
                _onToken(Marshal.PtrToStringUTF8(tok) ?? "");
            };
            int rc = Native.ai_generate(prompt, paramsJson, cb, IntPtr.Zero, out IntPtr err);
            if (rc != 0 && err != IntPtr.Zero)
            {
                var msg = Marshal.PtrToStringUTF8(err);
                Native.ai_free_string(err);
                throw new InvalidOperationException($"generate failed: {msg}");
            }
        }, ct);
    }
}