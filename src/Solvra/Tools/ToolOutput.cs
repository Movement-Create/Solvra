#nullable enable

namespace Solvra.Tools;

/// <summary>
/// Keeps tool results small enough for the model's context. Large outputs keep their head and
/// tail (errors are usually at the end), and the full text is saved to a temp file whose path is
/// given to the model so it can grep or page through it instead of re-running the command.
/// </summary>
public static class ToolOutput
{
    /// <summary>Default per-result budget in characters (~7.5k tokens).</summary>
    public const int DefaultMaxChars = 30_000;

    /// <summary>Longest single line kept before clipping (minified files, base64, logs).</summary>
    public const int MaxLineChars = 2_000;

    public static int MaxChars =>
        int.TryParse(Environment.GetEnvironmentVariable("SOLVRA_TOOL_OUTPUT_MAX_CHARS"), out var v) && v >= 2_000 ? v : DefaultMaxChars;

    public static string Limit(string text, string label, int? maxChars = null)
    {
        var max = maxChars ?? MaxChars;
        text = ClipLongLines(text);
        if (text.Length <= max) return text;

        var spill = Spill(text, label);
        var head = text[..(max * 2 / 3)];
        var tail = text[^(max / 3)..];
        var omitted = text.Length - head.Length - tail.Length;
        var where = spill != null ? $" Full output saved to {spill} (use grep/file_read with offset to inspect)." : "";
        return $"{head}\n\n[... {omitted} characters omitted.{where} ...]\n\n{tail}";
    }

    public static string ClipLongLines(string text)
    {
        if (text.Length <= MaxLineChars) return text;
        var lines = text.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > MaxLineChars)
            {
                lines[i] = lines[i][..MaxLineChars] + $"… [line clipped, {lines[i].Length} chars]";
                changed = true;
            }
        }
        return changed ? string.Join('\n', lines) : text;
    }

    private static string? Spill(string text, string label)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "solvra-tool-output");
            Directory.CreateDirectory(dir);
            var safe = new string(label.Where(char.IsLetterOrDigit).Take(20).ToArray());
            var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safe}-{Guid.NewGuid():N}"[..48] + ".txt");
            File.WriteAllText(path, text);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
