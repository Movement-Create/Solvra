#nullable enable

namespace Solvra.Observability;

/// <summary>
/// Console output formatting with ANSI colors.
/// Subscribes to Tracer.OnSpanEvent for observability output.
/// </summary>
public static class Printer
{
    // No ANSI codes when output is piped or NO_COLOR is set (logs stayed full of escape codes).
    private static readonly bool UseColor =
        !Console.IsErrorRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    private static string C(string code) => UseColor ? code : "";
    private static readonly string Reset = C("\x1b[0m");
    private static readonly string Bold = C("\x1b[1m");
    private static readonly string Dim = C("\x1b[2m");
    private static readonly string Red = C("\x1b[31m");
    private static readonly string Green = C("\x1b[32m");
    private static readonly string Yellow = C("\x1b[33m");
    private static readonly string Blue = C("\x1b[34m");
    private static readonly string Magenta = C("\x1b[35m");
    private static readonly string Cyan = C("\x1b[36m");
    private static readonly string Gray = C("\x1b[90m");

    /// <summary>One-line description of a tool call for progress output and audit logs.</summary>
    public static string SummarizeInput(string toolName, System.Text.Json.JsonElement input)
    {
        if (input.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
        string? Get(string name) => input.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        var summary = toolName switch
        {
            "bash" => Get("command"),
            "code_run" => $"{Get("language")} ({Get("code")?.Length ?? 0} chars)",
            "file_read" or "file_write" or "file_edit" => Get("path"),
            "grep" => $"/{Get("pattern")}/ {Get("path") ?? ""} {Get("glob") ?? ""}".Trim(),
            "glob" => $"{Get("pattern")} {Get("path") ?? ""}".Trim(),
            "web_fetch" => Get("url"),
            "web_search" => Get("query"),
            "agent" => Get("prompt"),
            "todo" => Get("action"),
            _ => null
        } ?? input.GetRawText();
        summary = summary.Replace('\n', ' ');
        return summary.Length > 160 ? summary[..160] + "\u2026" : summary;
    }

    private static string Attr(SpanEvent evt, string key) =>
        evt.Span.Attributes?.TryGetValue(key, out var v) == true ? v?.ToString() ?? "" : "";

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
                    var toolName = Attr(evt, "tool") is { Length: > 0 } t ? t : Attr(evt, "tool_name");
                    var summary = Attr(evt, "summary");
                    Console.Error.WriteLine($"{Magenta}[tool]{Reset} {Bold}{toolName}{Reset}{(summary.Length > 0 ? $" {Dim}{summary}{Reset}" : "")}");
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
                var endName = Attr(evt, "tool") is { Length: > 0 } t2 ? t2 : Attr(evt, "tool_name");
                Console.Error.WriteLine($"{Magenta}[tool]{Reset} {endName} done{durationStr}");
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
