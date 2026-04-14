#nullable enable

using System.Text.Json;
using System.Text.RegularExpressions;
using Solvra.Security;

namespace Solvra.Tools;

public class WebFetchTool : ToolBase
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Solvra/1.0" } }
    };

    public override string Name => "web_fetch";
    public override string Description => "Fetch content from a URL.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Network;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            url = new { type = "string", description = "URL to fetch" },
            prompt = new { type = "string", description = "What to extract from the page" }
        },
        required = new[] { "url" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var url = GetString(input, "url");
        var prompt = GetOptionalString(input, "prompt");

        if (string.IsNullOrWhiteSpace(url))
            return new ToolExecuteResult("Error: url is required", true);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new ToolExecuteResult("Error: invalid URL", true);

        try
        {
            var response = await HttpClient.GetAsync(uri, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            // Basic HTML to text conversion
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html") == true)
            {
                content = StripHtml(content);
            }

            // Truncate to 50KB
            if (content.Length > 50 * 1024)
            {
                content = content[..(50 * 1024)] + "\n[Content truncated at 50KB]";
            }

            var output = $"URL: {url}\nStatus: {(int)response.StatusCode}\n\n{content}";
            if (!string.IsNullOrEmpty(prompt))
                output = $"[Prompt: {prompt}]\n{output}";

            return new ToolExecuteResult(output, false);
        }
        catch (Exception ex)
        {
            return new ToolExecuteResult($"Error fetching URL: {ex.Message}", true);
        }
    }

    private static string StripHtml(string html)
    {
        // Remove script and style blocks
        html = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        // Remove tags
        html = Regex.Replace(html, @"<[^>]+>", " ");
        // Decode basic entities
        html = html.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");
        // Collapse whitespace
        html = Regex.Replace(html, @"\s+", " ").Trim();
        return html;
    }
}
