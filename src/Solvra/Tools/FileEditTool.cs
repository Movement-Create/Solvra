#nullable enable

using System.Text;
using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class FileEditTool : ToolBase
{
    public override string Name => "file_edit";

    public override string Description =>
        "Replace exact text in a file. old_string must match the file exactly (including indentation) and be " +
        "unique unless replace_all=true. Read the file first. Prefer this over file_write for changes to existing files. " +
        "Use an empty old_string only to create a new file.";

    public override PermissionLevel PermissionLevel => PermissionLevel.Write;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "File path to edit" },
            old_string = new { type = "string", description = "Exact text to find" },
            new_string = new { type = "string", description = "Replacement text" },
            replace_all = new { type = "boolean", description = "Replace every occurrence (default false)" }
        },
        required = new[] { "path", "old_string", "new_string" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var path = GetString(input, "path");
        var oldString = GetString(input, "old_string");
        var newString = GetString(input, "new_string");
        var replaceAll = input.TryGetProperty("replace_all", out var ra) && ra.ValueKind == JsonValueKind.True;

        if (string.IsNullOrWhiteSpace(path))
            return new ToolExecuteResult("Error: path is required", true);
        if (!Path.IsPathRooted(path))
            path = Path.Combine(context.Cwd, path);

        if (oldString.Length == 0)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return new ToolExecuteResult("Error: old_string is empty but the file exists and is not empty. Provide the text to replace.", true);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, newString, new UTF8Encoding(false), ct);
            return new ToolExecuteResult($"Created {path} ({CountLines(newString)} lines)", false);
        }

        if (!File.Exists(path))
            return new ToolExecuteResult($"Error: file not found: {path}", true);
        if (oldString == newString)
            return new ToolExecuteResult("Error: old_string and new_string are identical; nothing to change.", true);

        var (content, encoding) = await ReadPreservingEncodingAsync(path, ct);

        // Models usually send "\n"; keep a CRLF file CRLF.
        var crlf = content.Contains("\r\n");
        if (crlf && !oldString.Contains("\r\n"))
        {
            oldString = oldString.Replace("\n", "\r\n");
            newString = newString.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        var occurrences = CountOccurrences(content, oldString);
        if (occurrences == 0)
            return new ToolExecuteResult("Error: old_string not found in file." + NearMissHint(content, oldString), true);
        if (occurrences > 1 && !replaceAll)
            return new ToolExecuteResult($"Error: old_string found {occurrences} times. Include more surrounding lines to make it unique, or set replace_all=true.", true);

        var firstIndex = content.IndexOf(oldString, StringComparison.Ordinal);
        var newContent = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : content[..firstIndex] + newString + content[(firstIndex + oldString.Length)..];

        await File.WriteAllTextAsync(path, newContent, encoding, ct);

        var startLine = content[..firstIndex].Count(c => c == '\n') + 1;
        var summary = replaceAll && occurrences > 1
            ? $"Replaced {occurrences} occurrences in {path}."
            : $"Edited {path} at line {startLine}.";
        return new ToolExecuteResult(summary + "\n" + Snippet(newContent, startLine, CountLines(newString)), false);
    }

    internal static async Task<(string Content, Encoding Encoding)> ReadPreservingEncodingAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var encoding = new UTF8Encoding(hasBom);
        var text = hasBom ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
        return (text, encoding);
    }

    private static int CountLines(string s) => s.Length == 0 ? 0 : s.Count(c => c == '\n') + (s.EndsWith('\n') ? 0 : 1);

    private static string Snippet(string content, int startLine, int changedLines)
    {
        var lines = content.Split('\n');
        var from = Math.Max(1, startLine - 3);
        var to = Math.Min(lines.Length, startLine + Math.Max(changedLines, 1) + 2);
        var sb = new StringBuilder();
        for (var i = from; i <= to && i - from < 40; i++)
            sb.Append(i).Append('\t').Append(ToolOutput.ClipLongLines(lines[i - 1].TrimEnd('\r'))).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>When the exact text is missing, point at a line that matches after trimming whitespace.</summary>
    private static string NearMissHint(string content, string oldString)
    {
        var firstLine = oldString.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (firstLine == null) return "";
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == firstLine)
                return $" A line matching \"{Trunc(firstLine)}\" exists at line {i + 1} with different whitespace or following lines; re-read the file and copy the text exactly.";
        }
        return " Re-read the file (file_read) and copy the text exactly, including indentation.";
    }

    private static string Trunc(string s) => s.Length > 80 ? s[..80] + "…" : s;

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
