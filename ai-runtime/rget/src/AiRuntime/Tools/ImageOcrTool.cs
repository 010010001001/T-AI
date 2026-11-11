// src/AiRuntime/Tools/ImageOcrTool.cs
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

/// <summary>
/// 图像识别（离线 OCR）。使用 Windows 自带 OCR 引擎，不依赖额外模型。
/// 输入：{"path":"C:\\...\\image.png"}
/// 输出：{"text":"...","lines":N,"language":"zh-Hans"}
/// </summary>
public sealed class ImageOcrTool : IManagedTool
{
    public string Name => "image_ocr";
    public string[] Functions => new[] { "ocr", "recognize" };
    public bool RequiresPermission => true;

    public string Execute(string function, string inputJson)
    {
        string? path = null;
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.TryGetProperty("path", out var p))
                path = p.GetString();
        }
        catch (JsonException)
        {
            return Error("invalid json");
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Error($"image not found: {path}");

        try
        {
            return Recognize(path!);
        }
        catch (Exception ex)
        {
            return Error(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string Recognize(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        using var stream = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult();
        var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();

        using var bitmap = decoder
            .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask().GetAwaiter().GetResult();

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-CN"))
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

        if (engine is null)
            return Error("no OCR language available");

        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

        var payload = new
        {
            text = result.Text,
            lines = result.Lines.Count,
            language = engine.RecognizerLanguage.LanguageTag
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
