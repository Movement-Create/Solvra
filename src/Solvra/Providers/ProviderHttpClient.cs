namespace Solvra.Providers;

/// <summary>Creates provider clients without inheriting HttpClient's hidden 100-second default.</summary>
internal static class ProviderHttpClient
{
    public const int DefaultTimeoutSeconds = 600;
    public const int MaxTimeoutSeconds = 86_400;

    public static HttpClient Create(int timeoutSeconds)
        => new() { Timeout = ToTimeSpan(timeoutSeconds) };

    internal static TimeSpan ToTimeSpan(int timeoutSeconds)
    {
        if (timeoutSeconds == 0) return Timeout.InfiniteTimeSpan;
        if (timeoutSeconds is < 0 or > MaxTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds),
                $"Model timeout must be 0 (disabled) or between 1 and {MaxTimeoutSeconds} seconds.");
        return TimeSpan.FromSeconds(timeoutSeconds);
    }
}
