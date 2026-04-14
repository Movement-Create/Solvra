#nullable enable

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Solvra.Security;

namespace Solvra.Tools;

public class WebSearchTool : ToolBase
{
    private static readonly HttpClient HttpClient = new()
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

        // Extract result links from DuckDuckGo Lite HTML
        var linkPattern = new Regex(@"<a[^>]+rel=""nofollow""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline);
        var snippetPattern = new Regex(@"<td[^>]*class=""result-snippet""[^>]*>(.*?)</td>", RegexOptions.Singleline);

        var links = linkPattern.Matches(html);
        var snippets = snippetPattern.Matches(html);

        for (var i = 0; i < Math.Min(links.Count, maxResults); i++)
        {
            var title = StripTags(links[i].Groups[2].Value).Trim();
            var url = links[i].Groups[1].Value;
            var snippet = i < snippets.Count ? StripTags(snippets[i].Groups[1].Value).Trim() : "";

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
                results.Add(new SearchResult(title, url, snippet));
        }

        return results;
    }

    private static string StripTags(string html)
    {
        return Regex.Replace(html, @"<[^>]+>", "").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");
    }
}
