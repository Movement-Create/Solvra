#nullable enable

using System.Text.RegularExpressions;

namespace Solvra.Scheduler;

public class CronScheduler : IAsyncDisposable
{
    private readonly Dictionary<string, CronJobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    /// <summary>
    /// Delegate invoked to run an agent. Parameters: (prompt, title) -> result text
    /// </summary>
    public Func<string, string, CancellationToken, Task<string>>? RunAgentDelegate { get; set; }

    /// <summary>
    /// Optional callback for logging cron output to memory.
    /// </summary>
    public Func<string, string, string?, Task>? LogToMemoryDelegate { get; set; }

    public void AddJob(Config.CronJobConfig config)
    {
        if (!ValidateCronExpression(config.Schedule))
            throw new ArgumentException($"Invalid cron expression: {config.Schedule}");

        _jobs[config.Name] = new CronJobState(config, null);
    }

    public void RemoveJob(string name) => _jobs.Remove(name);

    public IReadOnlyList<Config.CronJobConfig> ListJobs() => _jobs.Values.Select(j => j.Config).ToList();

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _runLoop = RunLoopAsync(_cts.Token);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runLoop = RunLoopAsync(_cts.Token);
        await _runLoop;
    }

    public async Task StopAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            if (_runLoop != null)
            {
                try { await _runLoop; } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Fix 6i: Reduce polling to 15 seconds for more precise scheduling
                await Task.Delay(TimeSpan.FromSeconds(15), ct);

                var now = DateTime.UtcNow;
                foreach (var (name, state) in _jobs)
                {
                    if (!state.Config.Enabled) continue;
                    if (ShouldRun(state.Config.Schedule, now, state.LastRun))
                    {
                        _jobs[name] = state with { LastRun = now };
                        _ = RunJobAsync(state.Config, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Cron] Loop error: {ex.Message}");
            }
        }
    }

    private async Task RunJobAsync(Config.CronJobConfig config, CancellationToken ct)
    {
        try
        {
            if (RunAgentDelegate == null) return;

            var result = await RunAgentDelegate(config.Prompt, $"Cron: {config.Name}", ct);

            if (LogToMemoryDelegate != null)
            {
                await LogToMemoryDelegate($"### Cron Job: {config.Name}\n{result}", config.Name, null);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Cron] Job \"{config.Name}\" failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Cron matching: checks if the current minute matches the cron expression.
    /// Supports 5-field cron: minute hour day-of-month month day-of-week.
    /// </summary>
    public static bool ShouldRun(string cronExpr, DateTime now, DateTime? lastRun)
    {
        // Don't run if we ran within the last 55 seconds
        if (lastRun.HasValue && (now - lastRun.Value).TotalSeconds < 55)
            return false;

        var fields = cronExpr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return false;

        return FieldMatches(fields[0], now.Minute, 0, 59)
            && FieldMatches(fields[1], now.Hour, 0, 23)
            && FieldMatches(fields[2], now.Day, 1, 31)
            && FieldMatches(fields[3], now.Month, 1, 12)
            && FieldMatches(fields[4], (int)now.DayOfWeek, 0, 6);
    }

    /// <summary>
    /// Fix 6i: Support combination cron expressions like "*/5,10-20" or "1,5,10-15".
    /// Split on comma first, then evaluate each sub-expression.
    /// </summary>
    internal static bool FieldMatches(string field, int value, int min, int max)
    {
        if (field == "*") return true;

        // Fix 6i: Handle comma-separated combination expressions
        if (field.Contains(','))
        {
            return field.Split(',').Any(part => EvaluateFieldPart(part.Trim(), value, min, max));
        }

        return EvaluateFieldPart(field, value, min, max);
    }

    private static bool EvaluateFieldPart(string part, int value, int min, int max)
    {
        // Handle step: */N
        if (part.StartsWith("*/"))
        {
            if (int.TryParse(part[2..], out var step) && step > 0)
                return value % step == 0;
            return false;
        }

        // Handle range with optional step: A-B or A-B/N
        if (part.Contains('-'))
        {
            var stepSplit = part.Split('/');
            var rangePart = stepSplit[0];
            var step = stepSplit.Length > 1 && int.TryParse(stepSplit[1], out var s) ? s : 1;

            var rangeParts = rangePart.Split('-');
            if (rangeParts.Length == 2 &&
                int.TryParse(rangeParts[0], out var low) &&
                int.TryParse(rangeParts[1], out var high))
            {
                if (value < low || value > high) return false;
                return (value - low) % step == 0;
            }
            return false;
        }

        // Single value
        return int.TryParse(part, out var exact) && exact == value;
    }

    /// <summary>
    /// Fix 6i: Updated validation to support combination expressions.
    /// </summary>
    public static bool ValidateCronExpression(string expression)
    {
        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return false;

        var pattern = new Regex(@"^(\*|(\*/\d+)|\d+(-\d+(/\d+)?)?)(,(\*|(\*/\d+)|\d+(-\d+(/\d+)?)?))*$");
        return fields.All(f => pattern.IsMatch(f));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private record CronJobState(Config.CronJobConfig Config, DateTime? LastRun);
}
