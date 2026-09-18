using System.Net;
using System.Text.Json;

namespace Solvra.Providers;

/// <summary>
/// Turns a failed streaming response into an exception that carries the provider's own
/// error message (e.g. "Weekly usage limit reached"), instead of a bare status code.
/// </summary>
public static class HttpErrors
{
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string provider, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch { body = ""; }
        throw new HttpRequestException(Describe(provider, response.StatusCode, body), null, response.StatusCode);
    }

    /// <summary>Build a readable one-line error from a status code and an (optionally JSON) error body.</summary>
    public static string Describe(string provider, HttpStatusCode status, string body)
    {
        var detail = ExtractMessage(body);
        var text = $"{provider} API error {(int)status}";
        return string.IsNullOrWhiteSpace(detail) ? text : $"{text}: {detail}";
    }

    internal static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String) return err.GetString();
                    if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        return m.GetString();
                }
                if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String) return msg.GetString();
            }
        }
        catch (JsonException) { /* not JSON: fall through to the raw text */ }
        var trimmed = body.Trim();
        return trimmed.Length > 500 ? trimmed[..500] + "…" : trimmed;
    }
}
