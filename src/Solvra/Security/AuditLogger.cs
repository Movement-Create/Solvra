#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solvra.Security;

public record AuditEvent(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("trace_id")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TraceId,
    [property: JsonPropertyName("data")] JsonElement? Data);

public class AuditLogger : IAsyncDisposable
{
    // Keep commands readable in the log ("&&", quotes, ">" instead of \u0026 escapes).
    private static readonly System.Text.Json.JsonSerializerOptions ReadableJson =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Trace id of the run in progress on this async flow; stamped on every event.</summary>
    public static readonly AsyncLocal<string?> CurrentTraceId = new();

    private readonly string _logDirectory;
    private string _logPath;

    /// <summary>Overridable for tests of the daily rollover.</summary>
    internal Func<DateTime> Clock = () => DateTime.UtcNow;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private StreamWriter? _writer;

    public AuditLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logDirectory = logDirectory;
        _logPath = PathFor(DateTime.UtcNow);
    }

    /// <summary>File for the day the event happened, not the day the process started.</summary>
    public string LogPath => _logPath;

    private string PathFor(DateTime utc) => Path.Combine(_logDirectory, $"audit-{utc:yyyy-MM-dd}.jsonl");

    private async Task EnsureWriterAsync()
    {
        var path = PathFor(Clock());
        if (_writer != null && path != _logPath)
        {
            await _writer.DisposeAsync();
            _writer = null;
        }
        _logPath = path;
        _writer ??= new StreamWriter(
                new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                leaveOpen: false)
            { AutoFlush = true };
    }

    public async Task LogAsync(string eventType, object? data = null, string? sessionId = null)
    {
        var jsonData = data != null
            ? JsonSerializer.SerializeToElement(data, ReadableJson)
            : (JsonElement?)null;

        var entry = new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: eventType,
            SessionId: sessionId,
            TraceId: CurrentTraceId.Value,
            Data: jsonData);

        var line = JsonSerializer.Serialize(entry, ReadableJson);

        await _lock.WaitAsync();
        try
        {
            await EnsureWriterAsync();
            await _writer!.WriteLineAsync(line);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task LogToolExecutionAsync(string sessionId, string toolName, bool isError, long durationMs, string? operationId = null)
    {
        return LogAsync("tool_execution_metrics", new
        {
            operation_id = operationId,
            tool = toolName,
            is_error = isError,
            duration_ms = durationMs
        }, sessionId);
    }

    public Task LogSessionStartAsync(string sessionId, string? title = null)
    {
        return LogAsync("session_start", new { title }, sessionId);
    }

    public Task LogSessionEndAsync(string sessionId, int turns, double? costUsd = null)
    {
        return LogAsync("session_end", new { turns, cost_usd = costUsd }, sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer != null)
        {
            await _writer.DisposeAsync();
            _writer = null;
        }
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
