#nullable enable

using System.Text.RegularExpressions;

namespace Solvra.Scheduler;

public record CronJobConfig(
    string Name,
    string Schedule,
    string Prompt,
    bool Enabled = true);

public class CronScheduler : IAsyncDisposable
{
    private readonly Dictionary<string, CronJobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    /// <summary>
    /// Delegate invoked to run an agent. Parameters: (prompt, title) → result text
    /// </summary>
    public Func<string, string, CancellationToken, Task<string>>? RunAgentDelegate { get; set; }

    /// <summary>
    /// Optional callback for logging cron output to memory.
    /// </summary>
    public Func<string, string, string?, Task>? LogToMemoryDelegate { get; set; }

    public void AddJob(CronJobConfig config)
    {
        if (!ValidateCronExpression(config.Schedule))
            throw new ArgumentException($"Invalid cron expression: {config.Schedule}");

        _jobs[config.Name] = new CronJobState(config, null);
    }

    public void RemoveJob(string name) => _jobs.Remove(name);

    public IReadOnlyList<CronJobConfig> ListJobs() => _jobs.Values.Select(j => j.Config).ToList();

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _runLoop = RunLoopAsync(_cts.Token);
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
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

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

    private async Task RunJobAsync(CronJobConfig config, CancellationToken ct)
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
    /// Simple cron matching: checks if the current minute matches the cron expression.
    /// Supports 5-field cron: minute hour day-of-month month day-of-week.
    /// </summary>
    public static bool ShouldRun(string cronExpr, DateTime now, DateTime? lastRun)
    {
        // Don't run if we ran within the last minute
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

    private static bool FieldMatches(string field, int value, int min, int max)
    {
        if (field == "*") return true;

        // Handle step: */N
        if (field.StartsWith("*/"))
        {
            if (int.TryParse(field[2..], out var step) && step > 0)
                return value % step == 0;
            return false;
        }

        // Handle range: A-B
        if (field.Contains('-'))
        {
            var parts = field.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out var low) && int.TryParse(parts[1], out var high))
                return value >= low && value <= high;
            return false;
        }

        // Handle list: A,B,C
        if (field.Contains(','))
        {
            return field.Split(',').Any(v => int.TryParse(v.Trim(), out var n) && n == value);
        }

        // Single value
        return int.TryParse(field, out var exact) && exact == value;
    }

    public static bool ValidateCronExpression(string expression)
    {
        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return false;

        var pattern = new Regex(@"^(\*|(\*/\d+)|\d+(-\d+)?|\d+(,\d+)*)$");
        return fields.All(f => pattern.IsMatch(f));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private record CronJobState(CronJobConfig Config, DateTime? LastRun);
}
