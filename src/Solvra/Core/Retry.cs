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
    private static readonly HashSet<int> RetryableStatusCodes = [429, 500, 502, 503, 529];

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
        var message = ex.Message.ToLowerInvariant();

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

        // Check status codes mentioned in message
        foreach (var code in RetryableStatusCodes)
        {
            if (message.Contains(code.ToString()))
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
