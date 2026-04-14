#nullable enable

namespace Solvra.Observability;

/// <summary>
/// Console output formatting with ANSI colors.
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
        Console.WriteLine($"{Dim}{"".PadRight(60, '─')}{Reset}");

    public static void Header(string title) =>
        Console.WriteLine($"\n{Bold}{Cyan}{title}{Reset}\n");

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "…";
    }
}
