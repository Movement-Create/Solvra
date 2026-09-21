using System.Net;

namespace Solvra.Core;

public record RetryOptions
{
    public int MaxRetries { get; init; } = 3;
    public int BaseDelayMs { get; init; } = 1000;
    public int MaxDelayMs { get; init; } = 30000;
    public Func<int, Exception, Task<bool>>? OnRetry { get; init; }
}

public static class Retry
{
    private static readonly HashSet<int> RetryableStatusCodes = [408, 429, 500, 502, 503, 504, 529];

    /// <summary>Longest server-requested wait we honour; beyond it the error is surfaced.</summary>
    public const int MaxRetryAfterSeconds = 60;

    /// <summary>Quota/plan limits look like 429s but won't clear within a retry window.</summary>
    private static readonly string[] QuotaMessages =
    [
        "usage limit", "quota", "insufficient_quota", "billing", "credit balance", "limit reached", "resets in"
    ];

    private static readonly string[] RetryableMessages =
    [
        "overloaded",
        "rate limit",
        "too many requests"
    ];

    private static readonly string[] RetryableNetworkErrors =
    [
        "ECONNRESET",
        "ECONNREFUSED",
        "ETIMEDOUT",
        "ENOTFOUND",
        "connection was forcibly closed",
        "actively refused",
        "timed out",
        "name or service not known"
    ];

    public static async Task<T> WithRetryAsync<T>(
        Func<Task<T>> fn,
        RetryOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new RetryOptions();

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                return await fn();
            }
            catch (Exception ex) when (attempt < options.MaxRetries)
            {
                if (!IsRetryable(ex))
                    throw;

                var delay = ComputeDelay(attempt, options.BaseDelayMs, options.MaxDelayMs);
                if (ex.Data["RetryAfterSeconds"] is int retryAfter)
                {
                    if (retryAfter > MaxRetryAfterSeconds) throw;
                    delay = Math.Max(delay, retryAfter * 1000);
                }

                if (options.OnRetry != null)
                {
                    var shouldContinue = await options.OnRetry(attempt, ex);
                    if (!shouldContinue) throw;
                }

                await Task.Delay(delay, ct);
            }
        }

        // This shouldn't be reached, but just in case
        return await fn();
    }

    public static bool IsRetryable(Exception ex)
    {
        if (ex is OperationCanceledException) return false;
        var message = ex.Message.ToLowerInvariant();

        foreach (var q in QuotaMessages)
        {
            if (message.Contains(q)) return false;
        }

        // Transport failures (connection reset/refused, DNS, TLS, truncated responses) carry no
        // status code; they are worth retrying.
        if (ex is HttpRequestException { StatusCode: null } &&
            (ex.InnerException is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException
             || message.Contains("error occurred while sending") || message.Contains("response ended prematurely")))
            return true;
        if (ex.InnerException is IOException or System.Net.Sockets.SocketException)
            return true;

        // Check HTTP status codes
        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            if (RetryableStatusCodes.Contains((int)httpEx.StatusCode.Value))
                return true;

            // Explicitly non-retryable
            var code = (int)httpEx.StatusCode.Value;
            if (code is 400 or 401 or 403 or 404)
                return false;
        }

        // Status codes in messages like "OpenAI API error 503: ..." (not any number in the text).
        foreach (var code in RetryableStatusCodes)
        {
            if (message.Contains($"error {code}") || message.Contains($"status {code}") || message.Contains($"({code})"))
                return true;
        }

        // Check retryable message patterns
        foreach (var pattern in RetryableMessages)
        {
            if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check network errors
        foreach (var error in RetryableNetworkErrors)
        {
            if (message.Contains(error, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // IOException and similar network errors
        if (ex is IOException or System.Net.Sockets.SocketException)
            return true;

        return false;
    }

    internal static int ComputeDelay(int attempt, int baseDelayMs, int maxDelayMs)
    {
        var delay = (int)(baseDelayMs * Math.Pow(2, attempt));
        var jitter = Random.Shared.Next(0, 501); // 0-500ms jitter
        return Math.Min(delay + jitter, maxDelayMs);
    }
}
