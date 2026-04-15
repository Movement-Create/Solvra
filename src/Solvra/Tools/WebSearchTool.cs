#nullable enable

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Solvra.Security;

namespace Solvra.Tools;

public class WebSearchTool : ToolBase
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false // Don't follow DDG redirects automatically
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "Solvra/1.0" } }
    };

    public override string Name => "web_search";
    public override string Description => "Search the web using DuckDuckGo Lite.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Network;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Search query" },
            count = new { type = "integer", description = "Number of results (default 5)", @default = 5 }
        },
        required = new[] { "query" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var query = GetString(input, "query");
        var count = GetInt(input, "count", 5);

        if (string.IsNullOrWhiteSpace(query))
            return new ToolExecuteResult("Error: query is required", true);

        // Fix 6h: plan_mode support
        if (context.PlanMode)
            return new ToolExecuteResult($"Would search for: {query}", false);

        // P9: Brave Search API fallback
        var braveKey = Environment.GetEnvironmentVariable("BRAVE_API_KEY");
        if (!string.IsNullOrEmpty(braveKey))
            return await SearchBraveAsync(query, count, braveKey, ct);

        // P9: SerpAPI fallback
        var serpKey = Environment.GetEnvironmentVariable("SERPAPI_KEY");
        if (!string.IsNullOrEmpty(serpKey))
            return await SearchSerpApiAsync(query, count, serpKey, ct);

        // Fallback to DuckDuckGo
        return await SearchDdgAsync(query, count, ct);
    }

    private static async Task<ToolExecuteResult> SearchBraveAsync(string query, int count, string apiKey, CancellationToken ct)
    {
        try
        {
            var encoded = WebUtility.UrlEncode(query);
            var url = $"https://api.search.brave.com/res/v1/web/search?q={encoded}&count={count}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Subscription-Token", apiKey);
            request.Headers.Add("Accept", "application/json");

            var response = await HttpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            var doc = JsonDocument.Parse(json);
            var results = new List<SearchResult>();

            if (doc.RootElement.TryGetProperty("web", out var web) &&
                web.TryGetProperty("results", out var webResults))
            {
                foreach (var item in webResults.EnumerateArray())
                {
                    if (results.Count >= count) break;
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var itemUrl = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    var snippet = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(itemUrl))
                        results.Add(new SearchResult(title, itemUrl, snippet));
                }
            }

            if (results.Count == 0)
                return new ToolExecuteResult("No results found.", false);

            var output = string.Join("\n\n", results.Select((r, i) =>
                $"{i + 1}. {r.Title}\n   {r.Url}\n   {r.Snippet}"));

            return new ToolExecuteResult(output, false);
        }
        catch (Exception ex)
        {
            return new ToolExecuteResult($"Brave search error: {ex.Message}", true);
        }
    }

    private static async Task<ToolExecuteResult> SearchSerpApiAsync(string query, int count, string apiKey, CancellationToken ct)
    {
        try
        {
            var encoded = WebUtility.UrlEncode(query);
            var url = $"https://serpapi.com/search.json?q={encoded}&num={count}&api_key={apiKey}";

            var json = await HttpClient.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            var results = new List<SearchResult>();

            if (doc.RootElement.TryGetProperty("organic_results", out var organic))
            {
                foreach (var item in organic.EnumerateArray())
                {
                    if (results.Count >= count) break;
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var itemUrl = item.TryGetProperty("link", out var u) ? u.GetString() ?? "" : "";
                    var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(itemUrl))
                        results.Add(new SearchResult(title, itemUrl, snippet));
                }
            }

            if (results.Count == 0)
                return new ToolExecuteResult("No results found.", false);

            var output = string.Join("\n\n", results.Select((r, i) =>
                $"{i + 1}. {r.Title}\n   {r.Url}\n   {r.Snippet}"));

            return new ToolExecuteResult(output, false);
        }
        catch (Exception ex)
        {
            return new ToolExecuteResult($"SerpAPI search error: {ex.Message}", true);
        }
    }

    private static async Task<ToolExecuteResult> SearchDdgAsync(string query, int count, CancellationToken ct)
    {
        try
        {
            var encoded = WebUtility.UrlEncode(query);
            var url = $"https://lite.duckduckgo.com/lite/?q={encoded}";

            var response = await HttpClient.GetStringAsync(url, ct);

            var results = ParseDuckDuckGoResults(response, count);

            if (results.Count == 0)
                return new ToolExecuteResult("No results found.", false);

            var output = string.Join("\n\n", results.Select((r, i) =>
                $"{i + 1}. {r.Title}\n   {r.Url}\n   {r.Snippet}"));

            return new ToolExecuteResult(output, false);
        }
        catch (Exception ex)
        {
            return new ToolExecuteResult($"Search error: {ex.Message}", true);
        }
    }

    private record SearchResult(string Title, string Url, string Snippet);

    private static List<SearchResult> ParseDuckDuckGoResults(string html, int maxResults)
    {
        var results = new List<SearchResult>();

        // Fix 6h: Improved DDG Lite HTML parsing
        // DDG Lite wraps results in <table> rows with specific patterns
        var linkPattern = new Regex(
            @"<a[^>]+rel=""nofollow""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline);
        var snippetPattern = new Regex(
            @"<td[^>]*class=""result-snippet""[^>]*>(.*?)</td>",
            RegexOptions.Singleline);

        var links = linkPattern.Matches(html);
        var snippets = snippetPattern.Matches(html);

        for (var i = 0; i < Math.Min(links.Count, maxResults); i++)
        {
            var title = StripTags(links[i].Groups[2].Value).Trim();
            var rawUrl = links[i].Groups[1].Value;
            var snippet = i < snippets.Count ? StripTags(snippets[i].Groups[1].Value).Trim() : "";

            // Fix 6h: Unwrap DDG /l/?uddg= redirects to get real URLs
            var url = UnwrapDdgRedirect(rawUrl);

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
                results.Add(new SearchResult(title, url, snippet));
        }

        return results;
    }

    /// <summary>
    /// Fix 6h: Extract real URL from DDG redirect wrapper.
    /// DDG Lite uses /l/?uddg=URL&amp;rut=... format.
    /// </summary>
    internal static string UnwrapDdgRedirect(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        // Check for DDG redirect pattern
        if (url.Contains("/l/?") || url.Contains("/l?"))
        {
            // Extract uddg parameter
            var match = Regex.Match(url, @"[?&]uddg=([^&]+)");
            if (match.Success)
            {
                var decoded = WebUtility.UrlDecode(match.Groups[1].Value);
                return decoded;
            }
        }

        // Check if it's a relative DDG URL
        if (url.StartsWith("//duckduckgo.com/l/"))
        {
            var match = Regex.Match(url, @"[?&]uddg=([^&]+)");
            if (match.Success)
                return WebUtility.UrlDecode(match.Groups[1].Value);
        }

        return url;
    }

    private static string StripTags(string html)
    {
        return Regex.Replace(html, @"<[^>]+>", "")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#x27;", "'")
            .Replace("&nbsp;", " ");
    }
}
