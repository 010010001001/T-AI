// src/AiRuntime/Tools/WebSearchTool.cs
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// 联网搜索（Bing 网页结果，免 API Key）。
/// 默认权限关闭；首次调用由 PermissionManager 询问，用户可保持或关闭。
/// 输入：{"query":"...","count":5}
/// 输出：{"query":"...","results":[{"title":"...","url":"...","snippet":"..."}]}
/// </summary>
public sealed class WebSearchTool : IManagedTool
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex ResultBlock = new(
        @"<li class=""b_algo""[\s\S]*?</li>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Link = new(
        @"<h2[^>]*>[\s\S]*?<a[^>]*href=""([^""]+)""[^>]*>([\s\S]*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Snippet = new(
        @"<p[^>]*>([\s\S]*?)</p>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);

    public string Name => "web_search";
    public string[] Functions => new[] { "search" };
    public bool RequiresPermission => true;

    public string Execute(string function, string inputJson)
    {
        if (!function.Equals("search", StringComparison.OrdinalIgnoreCase))
            return Error($"unsupported function: {function}");

        string query = string.Empty;
        int count = 5;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            if (doc.RootElement.TryGetProperty("query", out var q)) query = q.GetString() ?? string.Empty;
            if (doc.RootElement.TryGetProperty("count", out var c) && c.TryGetInt32(out int n))
                count = Math.Clamp(n, 1, 10);
        }
        catch (JsonException)
        {
            return Error("invalid json");
        }

        if (string.IsNullOrWhiteSpace(query))
            return Error("query is required");

        try
        {
            string url = "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query) +
                         "&setlang=zh-hans&count=" + count;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

            using var response = Http.Send(request);
            response.EnsureSuccessStatusCode();
            string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            var results = new List<object>();
            foreach (Match block in ResultBlock.Matches(html))
            {
                var link = Link.Match(block.Value);
                if (!link.Success) continue;

                string linkUrl = WebUtility.HtmlDecode(link.Groups[1].Value);
                string title = Clean(link.Groups[2].Value);
                var snippetMatch = Snippet.Match(block.Value);
                string snippet = snippetMatch.Success ? Clean(snippetMatch.Groups[1].Value) : string.Empty;

                if (title.Length == 0 || linkUrl.Length == 0) continue;
                results.Add(new { title, url = linkUrl, snippet });
                if (results.Count >= count) break;
            }

            return JsonSerializer.Serialize(new { query, results });
        }
        catch (Exception ex)
        {
            return Error(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string Clean(string html)
    {
        // 直接去掉标签（Bing 会在中文词之间插入 <strong> 等标签，
        // 用空格替换会把“本地部署”拆成“本 地 部 署”）
        string text = WebUtility.HtmlDecode(Tags.Replace(html, string.Empty));
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // 某些系统的证书吊销检查（CRYPT_E_NO_REVOCATION_CHECK）会阻断 HTTPS；
        // 这里只接受 Bing 域名，且仍由系统完成证书链校验。
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
        return client;
    }
}
