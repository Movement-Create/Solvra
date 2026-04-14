#nullable enable

using System.Diagnostics;

namespace Solvra.Observability;

/// <summary>
/// Trace logging for debugging agent loop execution.
/// </summary>
public class Tracer
{
    private readonly string _component;
    private readonly bool _enabled;

    public Tracer(string component, bool enabled = false)
    {
        _component = component;
        _enabled = enabled || Environment.GetEnvironmentVariable("SOLVRA_TRACE") == "1";
    }

    public void Trace(string message)
    {
        if (!_enabled) return;
        var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        Console.Error.WriteLine($"\x1b[90m[{timestamp}] [{_component}] {message}\x1b[0m");
    }

    public void TraceToolCall(string toolName, long inputBytes)
    {
        Trace($"tool_call: {toolName} (input: {inputBytes}B)");
    }

    public void TraceToolResult(string toolName, bool isError, long durationMs)
    {
        Trace($"tool_result: {toolName} error={isError} duration={durationMs}ms");
    }

    public void TraceTurn(int turn, int tokensSoFar)
    {
        Trace($"turn={turn} tokens_so_far={tokensSoFar}");
    }

    public IDisposable TraceSpan(string operation)
    {
        return new SpanTracer(this, operation);
    }

    private class SpanTracer : IDisposable
    {
        private readonly Tracer _tracer;
        private readonly string _operation;
        private readonly Stopwatch _sw;

        public SpanTracer(Tracer tracer, string operation)
        {
            _tracer = tracer;
            _operation = operation;
            _sw = Stopwatch.StartNew();
            _tracer.Trace($"span_start: {_operation}");
        }

        public void Dispose()
        {
            _sw.Stop();
            _tracer.Trace($"span_end: {_operation} ({_sw.ElapsedMilliseconds}ms)");
        }
    }
}
