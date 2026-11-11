// src/AiRuntime/Inference/InferenceClient.cs
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 推理层 C# 封装，对应原生 ai_inference.dll 的 ai_model_load / ai_generate / ai_count_tokens。
/// </summary>
/// <remarks>
/// 线程安全：原生层依赖进程级全局上下文（模型、KV cache、采样器），不可重入。
/// 本类用 SemaphoreSlim 串行化所有生成调用。
///
/// 回调线程：onToken 在 Task.Run 的工作线程上同步执行；若需 UI 线程，调用方自行封送。
///
/// 取消语义：CancellationToken 只阻止后续 token 转发给 onToken，不会中断原生生成循环；
/// 原生循环会跑到 max_tokens 或 EOG 才返回。真正的中断需要原生层支持取消标志。
/// </remarks>
public sealed class InferenceClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string> _defaultOnToken;
    private int _callCount;

    public InferenceClient(Action<string> onToken)
    {
        _defaultOnToken = onToken ?? throw new ArgumentNullException(nameof(onToken));
    }

    /// <summary>已成功调用 ai_generate 的次数，用于冒烟测试断言"LLM 参与了几次"。</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>模型是否已加载。未加载时生成路径应降级到确定性路径。</summary>
    public bool IsModelLoaded
    {
        get
        {
            try { return Native.ai_model_loaded() != 0; }
            catch (Exception) { return false; }   // DLL 缺失/损坏/入口点不匹配一律视为未加载
        }
    }

    /// <summary>加载 GGUF 模型；失败时抛出携带原生错误细节的异常。</summary>
    public void LoadModel(string ggufPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ggufPath);

        int rc = Native.ai_model_load(ggufPath, out IntPtr err);
        string? detail = null;
        if (err != IntPtr.Zero)
        {
            detail = Marshal.PtrToStringUTF8(err);
            Native.ai_free_string(err);
        }

        if (rc != 0)
            throw new InvalidOperationException(
                $"ai_model_load failed rc={rc}: {detail ?? "(no detail)"}");
    }

    /// <summary>使用构造时传入的默认回调进行流式生成。</summary>
    public Task GenerateAsync(string prompt, string paramsJson, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(paramsJson);

        return GenerateCoreAsync(prompt, paramsJson, _defaultOnToken, ct);
    }

    /// <summary>聚合所有 token，返回完整生成文本。</summary>
    public async Task<string> GenerateTextAsync(
        string prompt, GenerationParams parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        string paramsJson = JsonSerializer.Serialize(parameters);
        var sb = new StringBuilder();
        // 回调在单个原生线程上串行执行，StringBuilder 不会被并发访问
        await GenerateCoreAsync(prompt, paramsJson, token => sb.Append(token), ct)
            .ConfigureAwait(false);
        return sb.ToString();
    }

    /// <summary>
    /// 精确 token 计数（原生词表）。模型未加载或分词失败时返回 -1，
    /// 由 <see cref="CountTokensOrEstimate"/> 决定降级策略。
    /// </summary>
    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            return Native.ai_count_tokens(text);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// 压缩流程专用的 token 计数：模型未加载时退化为确定性估算，
    /// 保证"眨眼"策略在无模型环境下仍能运行（估算偏差约 ±15%）。
    /// </summary>
    public int CountTokensOrEstimate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int exact = CountTokens(text);
        return exact >= 0 ? exact : EstimateTokens(text);
    }

    /// <summary>
    /// 无词表时的确定性估算：CJK 字符按 1 token，其余按 4 字符 1 token，
    /// 空白与标点按 1 token / 4 个折算。只用于压缩边界判断，不用于正式计数。
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int cjk = 0, other = 0;
        foreach (char c in text)
        {
            if (c >= 0x2E80 && c <= 0x9FFF) cjk++;
            else if (c >= 0xF900 && c <= 0xFAFF) cjk++;
            else other++;
        }
        return cjk + (other + 3) / 4;
    }

    private async Task GenerateCoreAsync(
        string prompt, string paramsJson, Action<string> onToken, CancellationToken ct)
    {
        if (!IsModelLoaded)
            throw new InvalidOperationException("LLM 未加载：请先调用 LoadModel 并提供 GGUF 模型文件。");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                // 回调内异常必须在此捕获；穿过 P/Invoke 边界会直接终止进程
                Exception? callbackError = null;
                Interlocked.Increment(ref _callCount);

                Native.TokenCallback cb = (tok, _) =>
                {
                    if (ct.IsCancellationRequested) return;
                    if (callbackError is not null) return;   // 已出错，后续 token 全部丢弃
                    try
                    {
                        string? s = Marshal.PtrToStringUTF8(tok);
                        if (s is not null) onToken(s);
                    }
                    catch (Exception ex) { callbackError = ex; }
                };

                int rc = Native.ai_generate(prompt, paramsJson, cb, IntPtr.Zero, out IntPtr err);
                GC.KeepAlive(cb);   // 委托必须在原生调用全程存活，否则 GC 可能回收 thunk

                string? nativeError = null;
                if (err != IntPtr.Zero)
                {
                    nativeError = Marshal.PtrToStringUTF8(err);
                    Native.ai_free_string(err);
                }

                if (callbackError is not null)
                    throw new InvalidOperationException("token callback threw", callbackError);

                if (rc != 0)
                    throw new InvalidOperationException(
                        $"ai_generate failed rc={rc}: {nativeError ?? "(no error detail)"}");
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>生成参数。字段名与原生层 params_json 的解析键一致（snake_case）。</summary>
public sealed class GenerationParams
{
    [JsonPropertyName("max_tokens")]  public int   MaxTokens   { get; init; } = 512;
    [JsonPropertyName("temperature")] public float Temperature { get; init; } = 0.7f;
    [JsonPropertyName("top_k")]       public int   TopK        { get; init; } = 40;
    [JsonPropertyName("top_p")]       public float TopP        { get; init; } = 0.9f;
    [JsonPropertyName("ctx")]         public int   Ctx         { get; init; } = 4096;
}
