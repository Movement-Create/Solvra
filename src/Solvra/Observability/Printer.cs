#nullable enable

namespace Solvra.Observability;

/// <summary>
/// Console output formatting with ANSI colors.
/// Subscribes to Tracer.OnSpanEvent for observability output.
/// </summary>
public static class Printer
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";
    private const string Red = "\x1b[31m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Blue = "\x1b[34m";
    private const string Magenta = "\x1b[35m";
    private const string Cyan = "\x1b[36m";
    private const string Gray = "\x1b[90m";

    private static ObservabilityLevel _level = ObservabilityLevel.Normal;

    public static void SetLevel(ObservabilityLevel level) => _level = level;

    public static void Info(string message) =>
        Console.WriteLine($"{Blue}[info]{Reset} {message}");

    public static void Success(string message) =>
        Console.WriteLine($"{Green}[ok]{Reset} {message}");

    public static void Warn(string message) =>
        Console.WriteLine($"{Yellow}[warn]{Reset} {message}");

    public static void Error(string message) =>
        Console.Error.WriteLine($"{Red}[error]{Reset} {message}");

    public static void Debug(string message) =>
        Console.WriteLine($"{Gray}[debug]{Reset} {message}");

    public static void ToolCall(string toolName, string? input = null)
    {
        Console.Write($"{Magenta}[tool]{Reset} {Bold}{toolName}{Reset}");
        if (input != null)
            Console.Write($" {Dim}{Truncate(input, 100)}{Reset}");
        Console.WriteLine();
    }

    public static void ToolResult(string toolName, bool isError, string output)
    {
        var color = isError ? Red : Green;
        var status = isError ? "error" : "ok";
        Console.WriteLine($"{color}[{status}]{Reset} {toolName}: {Truncate(output, 200)}");
    }

    public static void AgentThinking()
    {
        Console.Write($"{Cyan}[thinking]{Reset} ");
    }

    public static void AgentResponse(string text)
    {
        Console.WriteLine(text);
    }

    public static void Separator() =>
        Console.WriteLine($"{Dim}{"".PadRight(60, '\u2500')}{Reset}");

    public static void Header(string title) =>
        Console.WriteLine($"\n{Bold}{Cyan}{title}{Reset}\n");

    /// <summary>
    /// Handler for Tracer.OnSpanEvent. Wire with: tracer.OnSpanEvent += Printer.HandleSpanEvent;
    /// </summary>
    public static void HandleSpanEvent(SpanEvent evt)
    {
        if (_level == ObservabilityLevel.Off) return;

        switch (evt.EventType)
        {
            case "span_start":
                FormatSpanStart(evt);
                break;
            case "span_end":
                FormatSpanEnd(evt);
                break;
            case "span_event":
                if (_level >= ObservabilityLevel.Verbose)
                    Console.Error.WriteLine($"{Gray}[event] {evt.EventName}{Reset}");
                break;
        }
    }

    private static void FormatSpanStart(SpanEvent evt)
    {
        var name = evt.Span.Name;

        switch (name)
        {
            case "agent.session":
                if (_level >= ObservabilityLevel.Normal)
                    Console.Error.WriteLine($"{Cyan}[session]{Reset} Starting agent session");
                break;
            case "agent.turn":
                if (_level >= ObservabilityLevel.Verbose)
                    Console.Error.WriteLine($"{Blue}[turn]{Reset} New turn");
                break;
            case "llm.call":
                if (_level >= ObservabilityLevel.Verbose)
                    Console.Error.WriteLine($"{Yellow}[llm]{Reset} Calling LLM...");
                break;
            case "tool.execute":
                if (_level >= ObservabilityLevel.Normal)
                {
                    var toolName = evt.Span.Attributes?.TryGetValue("tool_name", out var tn) == true
                        ? tn?.ToString() ?? "unknown" : "unknown";
                    Console.Error.WriteLine($"{Magenta}[tool]{Reset} {Bold}{toolName}{Reset}");
                }
                break;
            default:
                if (_level >= ObservabilityLevel.Debug)
                    Console.Error.WriteLine($"{Gray}[span] {name}{Reset}");
                break;
        }
    }

    private static void FormatSpanEnd(SpanEvent evt)
    {
        if (_level < ObservabilityLevel.Verbose) return;

        var name = evt.Span.Name;
        var durationStr = evt.DurationMs.HasValue ? $" ({evt.DurationMs}ms)" : "";

        switch (name)
        {
            case "agent.session":
                Console.Error.WriteLine($"{Cyan}[session]{Reset} Session complete{durationStr}");
                break;
            case "tool.execute":
                var toolName = evt.Span.Attributes?.TryGetValue("tool_name", out var tn) == true
                    ? tn?.ToString() ?? "unknown" : "unknown";
                Console.Error.WriteLine($"{Magenta}[tool]{Reset} {toolName} done{durationStr}");
                break;
            default:
                if (_level >= ObservabilityLevel.Debug)
                    Console.Error.WriteLine($"{Gray}[span_end] {name}{durationStr}{Reset}");
                break;
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "\u2026";
    }
}
