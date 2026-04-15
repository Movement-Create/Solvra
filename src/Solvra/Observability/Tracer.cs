#nullable enable

using System.Collections.Concurrent;
using System.Text.Json;

namespace Solvra.Observability;

/// <summary>
/// OTel-shaped tracing with AsyncLocal context propagation.
/// Writes span_start/span_end events to JSONL files.
/// </summary>
public record SpanContext(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    DateTime StartTime,
    Dictionary<string, object>? Attributes = null);

public enum ObservabilityLevel
{
    Off,
    Normal,
    Verbose,
    Debug
}

public class Tracer
{
    private static readonly AsyncLocal<SpanContext?> _current = new();
    private readonly string _outputPath;
    private readonly ObservabilityLevel _level;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string? _rootTraceId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    /// <summary>
    /// Fired on every span start/end event. Printer subscribes to this.
    /// </summary>
    public event Action<SpanEvent>? OnSpanEvent;

    public SpanContext? CurrentSpan => _current.Value;
    public ObservabilityLevel Level => _level;

    public Tracer(string? outputPath = null, ObservabilityLevel level = ObservabilityLevel.Normal)
    {
        _outputPath = outputPath ?? "sessions";
        _level = level;
    }

    public Tracer(string? outputPath, string sessionId, ObservabilityLevel level = ObservabilityLevel.Normal)
    {
        _outputPath = outputPath != null
            ? Path.Combine(outputPath, $"{sessionId}.spans.jsonl")
            : Path.Combine("sessions", $"{sessionId}.spans.jsonl");
        _level = level;
        _rootTraceId = Guid.NewGuid().ToString("N")[..16];
    }

    /// <summary>
    /// Start a new span. Returns IDisposable; disposing writes span_end.
    /// Automatically nests via AsyncLocal.
    /// Standard span names: agent.session, agent.turn, llm.call, tool.execute
    /// </summary>
    public IDisposable StartSpan(string name, Dictionary<string, object>? attributes = null)
    {
        if (_level == ObservabilityLevel.Off)
            return NullDisposable.Instance;

        var parent = _current.Value;
        var traceId = parent?.TraceId ?? _rootTraceId ?? Guid.NewGuid().ToString("N")[..16];
        var spanId = Guid.NewGuid().ToString("N")[..16];
        var parentSpanId = parent?.SpanId;

        var span = new SpanContext(traceId, spanId, parentSpanId, name, DateTime.UtcNow, attributes);
        _current.Value = span;

        // Emit span_start
        var startEvent = new SpanEvent("span_start", span, null);
        EmitEvent(startEvent);
        OnSpanEvent?.Invoke(startEvent);

        return new SpanScope(this, span, parent);
    }

    public void AddEvent(string name, Dictionary<string, object>? attributes = null)
    {
        if (_level == ObservabilityLevel.Off) return;

        var current = _current.Value;
        if (current == null) return;

        var evt = new SpanEvent("span_event", current, null, name, attributes);
        EmitEvent(evt);
        OnSpanEvent?.Invoke(evt);
    }

    // Legacy convenience methods for backward compat
    public void Trace(string message)
    {
        if (_level < ObservabilityLevel.Debug) return;
        var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        Console.Error.WriteLine($"\x1b[90m[{timestamp}] {message}\x1b[0m");
    }

    private void EmitEvent(SpanEvent evt)
    {
        if (_level == ObservabilityLevel.Off) return;

        try
        {
            var dir = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new
            {
                type = evt.EventType,
                timestamp = DateTime.UtcNow.ToString("o"),
                trace_id = evt.Span.TraceId,
                span_id = evt.Span.SpanId,
                parent_span_id = evt.Span.ParentSpanId,
                name = evt.Span.Name,
                start_time = evt.Span.StartTime.ToString("o"),
                duration_ms = evt.DurationMs,
                attributes = evt.Span.Attributes,
                event_name = evt.EventName,
                event_attributes = evt.EventAttributes
            }, JsonOptions);

            _writeLock.Wait();
            try
            {
                File.AppendAllText(_outputPath, json + "\n");
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // Tracing should never crash the agent
        }
    }

    internal void EndSpan(SpanContext span, SpanContext? parent)
    {
        _current.Value = parent;

        var durationMs = (long)(DateTime.UtcNow - span.StartTime).TotalMilliseconds;
        var endEvent = new SpanEvent("span_end", span, durationMs);
        EmitEvent(endEvent);
        OnSpanEvent?.Invoke(endEvent);
    }

    private sealed class SpanScope : IDisposable
    {
        private readonly Tracer _tracer;
        private readonly SpanContext _span;
        private readonly SpanContext? _parent;
        private bool _disposed;

        public SpanScope(Tracer tracer, SpanContext span, SpanContext? parent)
        {
            _tracer = tracer;
            _span = span;
            _parent = parent;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tracer.EndSpan(_span, _parent);
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

public record SpanEvent(
    string EventType,
    SpanContext Span,
    long? DurationMs,
    string? EventName = null,
    Dictionary<string, object>? EventAttributes = null);
