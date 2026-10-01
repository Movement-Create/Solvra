using System.Diagnostics;

namespace Solvra.Core;

/// <summary>
/// A run-scoped monotonic deadline shared by the parent agent, its tools, and subagents.
/// Wall-clock changes cannot extend or shorten the budget.
/// </summary>
public sealed class RunDeadline
{
    private readonly long _started = Stopwatch.GetTimestamp();

    public RunDeadline(TimeSpan limit)
    {
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limit));
        Limit = limit;
    }

    public TimeSpan Limit { get; }

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    public TimeSpan Remaining
    {
        get
        {
            var remaining = Limit - Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public bool IsExpired => Remaining == TimeSpan.Zero;

    public int CapTimeoutMilliseconds(int requestedMilliseconds)
    {
        var remainingMs = (long)Math.Floor(Remaining.TotalMilliseconds);
        if (remainingMs <= 0) return 1;
        return (int)Math.Clamp(Math.Min(requestedMilliseconds, remainingMs), 1, int.MaxValue);
    }

    public CancellationTokenSource CreateLinkedTokenSource(CancellationToken outer)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        linked.CancelAfter(Remaining);
        return linked;
    }
}
