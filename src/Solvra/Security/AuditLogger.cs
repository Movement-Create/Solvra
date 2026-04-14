#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solvra.Security;

public record AuditEvent(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("data")] JsonElement? Data);

public class AuditLogger : IAsyncDisposable
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private StreamWriter? _writer;

    public AuditLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
    }

    public string LogPath => _logPath;

    private Task EnsureWriterAsync()
    {
        _writer ??= new StreamWriter(
                new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                leaveOpen: false)
            { AutoFlush = true };
        return Task.CompletedTask;
    }

    public async Task LogAsync(string eventType, object? data = null, string? sessionId = null)
    {
        var jsonData = data != null
            ? JsonSerializer.SerializeToElement(data)
            : (JsonElement?)null;

        var entry = new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: eventType,
            SessionId: sessionId,
            Data: jsonData);

        var line = JsonSerializer.Serialize(entry);

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

    public Task LogToolExecutionAsync(string sessionId, string toolName, bool isError, long durationMs)
    {
        return LogAsync("tool_execution", new { tool = toolName, is_error = isError, duration_ms = durationMs }, sessionId);
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
