#nullable enable

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Solvra.Security;

namespace Solvra.Tools;

public class WebFetchTool : ToolBase
{
    private const int MaxBodyBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 5;

    private static readonly HttpClient HttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Solvra/1.0" } }
    };

    public override string Name => "web_fetch";

    public override string Description =>
        "Fetch an http(s) URL and return its text (HTML is converted to plain text; long pages are truncated). " +
        "Local and private network addresses are refused. Non-2xx responses are returned as errors.";

    public override PermissionLevel PermissionLevel => PermissionLevel.Network;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            url = new { type = "string", description = "http(s) URL to fetch" },
        },
        required = new[] { "url" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var url = GetString(input, "url");
        if (string.IsNullOrWhiteSpace(url))
            return new ToolExecuteResult("Error: url is required", true);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new ToolExecuteResult("Error: only absolute http(s) URLs are supported", true);

        try
        {
            HttpResponseMessage? response = null;
            for (var hop = 0; ; hop++)
            {
                if (await CheckDestinationAsync(uri, ct) is { } refusal)
                    return new ToolExecuteResult(refusal, true);

                response?.Dispose();
                response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                var code = (int)response.StatusCode;
                if (code is >= 300 and < 400 && response.Headers.Location is { } loc)
                {
                    if (hop >= MaxRedirects)
                        return new ToolExecuteResult($"Error: too many redirects (>{MaxRedirects})", true);
                    uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                    if (uri.Scheme is not ("http" or "https"))
                        return new ToolExecuteResult($"Error: redirect to unsupported scheme {uri.Scheme}", true);
                    continue;
                }
                break;
            }

            using (response)
            {
                var (content, truncatedBody) = await ReadCappedAsync(response, ct);
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (mediaType.Contains("html"))
                    content = StripHtml(content);

                var limited = ToolOutput.Limit(content, "web_fetch");
                if (truncatedBody) limited += $"\n[Body larger than {MaxBodyBytes} bytes; only the start was read.]";

                var status = (int)response.StatusCode;
                var output = $"URL: {uri}\nStatus: {status}\nContent-Type: {mediaType}\n\n{limited}";
                return new ToolExecuteResult(output, status >= 400);
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolExecuteResult("Error fetching URL: request timed out after 30 s", true);
        }
        catch (HttpRequestException ex)
        {
            return new ToolExecuteResult($"Error fetching URL: {ex.Message}", true);
        }
    }

    /// <summary>Refuse loopback, private, link-local and cloud-metadata destinations (SSRF guard).</summary>
    private static async Task<string?> CheckDestinationAsync(Uri uri, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("SOLVRA_WEBFETCH_ALLOW_PRIVATE") is "1" or "true")
            return null;

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal))
            addresses = [literal];
        else
        {
            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                return $"Error: refusing to fetch local address {uri.Host}";
            try { addresses = await Dns.GetHostAddressesAsync(uri.Host, ct); }
            catch (SocketException ex) { return $"Error: cannot resolve {uri.Host}: {ex.Message}"; }
        }

        foreach (var ip in addresses)
        {
            if (IsPrivate(ip))
                return $"Error: refusing to fetch {uri.Host} ({ip}): local/private network address. Set SOLVRA_WEBFETCH_ALLOW_PRIVATE=1 to allow.";
        }
        return null;
    }

    internal static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || b[0] == 127
            || b[0] == 0
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)          // link-local / cloud metadata
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT (incl. Tailscale)
    }

    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            total += read;
        var truncated = total > MaxBodyBytes;
        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding enc;
        try { enc = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); }
        catch { enc = Encoding.UTF8; }
        return (enc.GetString(buffer, 0, Math.Min(total, MaxBodyBytes)), truncated);
    }

    private static string StripHtml(string html)
    {
        html = Regex.Replace(html, @"<(script|style|noscript|svg)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<!--[\s\S]*?-->", "");
        html = Regex.Replace(html, @"<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", " ");
        html = WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"[ \t\f\v]+", " ");
        html = Regex.Replace(html, @"\s*\n\s*", "\n");
        return html.Trim();
    }
}
