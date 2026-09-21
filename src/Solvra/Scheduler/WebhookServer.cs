#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Solvra.Scheduler;

public class WebhookServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly string _secret;
    private readonly ConcurrentDictionary<string, RateLimitEntry> _rateLimits = new();
    private int _activeRequests;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    private const int MaxRequestsPerMinute = 30;
    private const int MaxConcurrentRequests = 5;
    private const int MaxBodyBytes = 1_048_576; // 1MB

    /// <summary>
    /// Delegate invoked to run an agent from a webhook trigger.
    /// Parameters: (prompt, sessionTitle) → (responseText, turns)
    /// </summary>
    public Func<string, string, CancellationToken, Task<(string Text, int Turns)>>? RunAgentDelegate { get; set; }

    /// <param name="host">
    /// Bind address. Defaults to loopback: put a reverse proxy (e.g. Tailscale Serve) in front
    /// for remote access. "+", "*" or "0.0.0.0" bind every interface.
    /// </param>
    public WebhookServer(int port = 7331, string? secret = null, string host = "127.0.0.1")
    {
        _secret = secret ?? Environment.GetEnvironmentVariable("SOLVRA_WEBHOOK_SECRET") ?? "";
        _listener = new HttpListener();
        // HttpListener matches the Host header against the prefix, so a "127.0.0.1" prefix would
        // reject requests forwarded by a local reverse proxy (Tailscale Serve keeps the public host
        // name). Listen with a wildcard prefix and enforce the bind address on the peer instead.
        _loopbackOnly = host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
        var prefixHost = _loopbackOnly || host is "0.0.0.0" or "*" or "+" ? "+" : host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        _listener.Prefixes.Add($"http://{prefixHost}:{port}/");
    }

    private readonly bool _loopbackOnly;

    private static bool SecretMatches(string? header, string secret)
    {
        var expected = System.Text.Encoding.UTF8.GetBytes($"Bearer {secret}");
        var actual = System.Text.Encoding.UTF8.GetBytes(header ?? "");
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _listenTask = ListenAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _listener.Stop();
            if (_listenTask != null)
            {
                try { await _listenTask; } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var httpContext = await _listener.GetContextAsync();
                _ = HandleRequestAsync(httpContext, ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext httpContext, CancellationToken ct)
    {
        var request = httpContext.Request;
        var response = httpContext.Response;

        try
        {
            if (_loopbackOnly && request.RemoteEndPoint is { } peer && !IPAddress.IsLoopback(peer.Address))
            {
                await WriteResponse(response, 403, new { error = "Forbidden: webhook accepts local connections only (use --host to change)." });
                return;
            }

            // Only accept POST /trigger
            if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/trigger")
            {
                await WriteResponse(response, 404, new { error = "Not found. Use POST /trigger." });
                return;
            }

            // Auth check
            if (!string.IsNullOrEmpty(_secret))
            {
                var authHeader = request.Headers["Authorization"];
                if (!SecretMatches(authHeader, _secret))
                {
                    await WriteResponse(response, 401, new { error = "Unauthorized" });
                    return;
                }
            }

            // Rate limiting
            var clientIp = GetClientIp(request);
            if (!CheckRateLimit(clientIp))
            {
                await WriteResponse(response, 429, new { error = "Rate limit exceeded. Max 30 requests/minute." });
                return;
            }

            // Concurrent request limit
            if (Interlocked.CompareExchange(ref _activeRequests, 0, 0) >= MaxConcurrentRequests)
            {
                await WriteResponse(response, 503, new { error = "Too many concurrent requests. Try again shortly." });
                return;
            }

            // Read body (with size limit)
            if (request.ContentLength64 > MaxBodyBytes)
            {
                await WriteResponse(response, 413, new { error = $"Body too large. Max {MaxBodyBytes} bytes." });
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(ct);
            }

            if (body.Length > MaxBodyBytes)
            {
                await WriteResponse(response, 413, new { error = "Body too large." });
                return;
            }

            // Parse body
            JsonElement bodyJson;
            try
            {
                bodyJson = JsonDocument.Parse(body).RootElement;
            }
            catch
            {
                await WriteResponse(response, 400, new { error = "Invalid JSON body." });
                return;
            }

            if (!bodyJson.TryGetProperty("prompt", out var promptElement) ||
                promptElement.ValueKind != JsonValueKind.String)
            {
                await WriteResponse(response, 400, new { error = "Missing 'prompt' field." });
                return;
            }

            var prompt = promptElement.GetString()!;

            if (RunAgentDelegate == null)
            {
                await WriteResponse(response, 500, new { error = "Agent runner not configured." });
                return;
            }

            Interlocked.Increment(ref _activeRequests);
            try
            {
                var title = $"Webhook: {prompt[..Math.Min(prompt.Length, 50)]}";
                var (text, turns) = await RunAgentDelegate(prompt, title, ct);

                await WriteResponse(response, 200, new
                {
                    text,
                    turns,
                });
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }
        catch (Exception ex)
        {
            try
            {
                await WriteResponse(response, 500, new { error = ex.Message });
            }
            catch { }
        }
        finally
        {
            response.Close();
        }
    }

    private bool CheckRateLimit(string clientIp)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entry = _rateLimits.GetOrAdd(clientIp, _ => new RateLimitEntry { Count = 0, ResetAt = now + 60_000 });

        lock (entry)
        {
            if (now > entry.ResetAt)
            {
                entry.Count = 1;
                entry.ResetAt = now + 60_000;
                return true;
            }

            entry.Count++;
            return entry.Count <= MaxRequestsPerMinute;
        }
    }

    private static string GetClientIp(HttpListenerRequest request)
    {
        var forwarded = request.Headers["X-Forwarded-For"];
        if (!string.IsNullOrEmpty(forwarded))
            return forwarded.Split(',')[0].Trim();

        return request.RemoteEndPoint?.Address.ToString() ?? "unknown";
    }

    private static async Task WriteResponse(HttpListenerResponse response, int statusCode, object body)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(body);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _listener.Close();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private class RateLimitEntry
    {
        public int Count;
        public long ResetAt;
    }
}
