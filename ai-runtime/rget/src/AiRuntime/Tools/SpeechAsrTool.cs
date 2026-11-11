// src/AiRuntime/Tools/SpeechAsrTool.cs
using System.Globalization;
using System.Speech.Recognition;
using System.Text.Json;

/// <summary>
/// 语音识别（离线）。使用 Windows 自带 SAPI 识别引擎，不依赖额外模型。
/// 输入：{"language":"zh-CN","timeout_ms":15000}
/// 输出：{"text":"...","culture":"zh-CN"}
/// </summary>
public sealed class SpeechAsrTool : IManagedTool
{
    public string Name => "speech_asr";
    public string[] Functions => new[] { "recognize", "listen" };
    public bool RequiresPermission => true;

    public string Execute(string function, string inputJson)
    {
        string language = "zh-CN";
        int timeoutMs = 15000;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            if (doc.RootElement.TryGetProperty("language", out var l))
                language = l.GetString() ?? language;
            if (doc.RootElement.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out int ms))
                timeoutMs = Math.Clamp(ms, 3000, 60000);
        }
        catch (JsonException)
        {
            return Error("invalid json");
        }

        try
        {
            var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
            var recognizer = recognizers.FirstOrDefault(r =>
                    r.Culture.Name.Equals(language, StringComparison.OrdinalIgnoreCase))
                ?? recognizers.FirstOrDefault();

            if (recognizer is null)
                return Error("no speech recognizer installed");

            using var engine = new SpeechRecognitionEngine(recognizer);
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();

            RecognitionResult? result = engine.Recognize(TimeSpan.FromMilliseconds(timeoutMs));
            return JsonSerializer.Serialize(new
            {
                text = result?.Text ?? string.Empty,
                culture = recognizer.Culture.Name
            });
        }
        catch (Exception ex)
        {
            return Error(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
