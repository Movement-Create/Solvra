#nullable enable

using System.Text;
using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class FileReadTool : ToolBase
{
    public const int DefaultLineLimit = 2000;

    public override string Name => "file_read";

    public override string Description =>
        $"Read a text file with line numbers. Returns at most {DefaultLineLimit} lines by default; use offset " +
        "(1-based line to start from) and limit to page through large files. Long lines are clipped. " +
        "Binary files are refused. For a directory, lists its entries.";

    public override PermissionLevel PermissionLevel => PermissionLevel.Read;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "File path to read (absolute or relative to the working directory)" },
            offset = new { type = "integer", description = "1-based line number to start from (default 1)" },
            limit = new { type = "integer", description = $"Maximum number of lines to read (default {DefaultLineLimit})" }
        },
        required = new[] { "path" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var path = GetString(input, "path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolExecuteResult("Error: path is required", true);

        if (!Path.IsPathRooted(path))
            path = Path.Combine(context.Cwd, path);

        if (Directory.Exists(path))
            return ListDirectory(path);

        if (!File.Exists(path))
            return new ToolExecuteResult($"Error: file not found: {path}", true);

        if (FileWalker.IsBinaryFile(path))
            return new ToolExecuteResult($"Error: {path} looks like a binary file ({new FileInfo(path).Length} bytes); not shown.", true);

        // offset is 1-based; 0 is accepted as "start" for callers that used the old 0-based form.
        var offset = Math.Max(1, GetOptionalInt(input, "offset") ?? 1);
        var limit = Math.Clamp(GetOptionalInt(input, "limit") ?? DefaultLineLimit, 1, 20_000);
        var maxChars = ToolOutput.MaxChars;
        var size = new FileInfo(path).Length;

        var sb = new StringBuilder();
        var lineNo = 0;
        var shown = 0;
        var stoppedForSize = false;
        using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lineNo++;
                if (lineNo < offset) continue;
                if (shown >= limit) { lineNo = -1; break; }
                if (sb.Length > maxChars) { stoppedForSize = true; lineNo = -1; break; }
                if (line.Length > ToolOutput.MaxLineChars)
                    line = line[..ToolOutput.MaxLineChars] + $"… [line clipped, {line.Length} chars]";
                sb.Append(offset + shown).Append('\t').Append(line).Append('\n');
                shown++;
            }
        }

        if (shown == 0)
        {
            var total = CountLines(path);
            return total == 0
                ? new ToolExecuteResult("(empty file)", false)
                : new ToolExecuteResult($"Error: offset {offset} is past the end of the file ({total} lines).", true);
        }

        var lastShown = offset + shown - 1;
        if (lineNo == -1)
        {
            var total = CountLines(path);
            var why = stoppedForSize ? "output size limit" : $"limit {limit}";
            sb.Append($"[Showing lines {offset}-{lastShown} of {total} ({why}). Use offset={lastShown + 1} to continue.]");
            // Large files: say so up front, since paging through them fills the context window.
            if (size > 1_000_000 && offset == 1)
                sb.Insert(0, $"[Large file: {total} lines, {size / 1024} KB. To find something, use grep (pattern, path) instead of reading it all.]\n");
        }
        return new ToolExecuteResult(sb.ToString().TrimEnd('\n'), false);
    }

    private static int CountLines(string path)
    {
        try { return File.ReadLines(path).Count(); } catch { return -1; }
    }

    private static ToolExecuteResult ListDirectory(string path)
    {
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
            .OrderBy(e => e is FileInfo)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Take(1000)
            .Select(e => e is DirectoryInfo ? e.Name + "/" : $"{e.Name}  ({((FileInfo)e).Length} bytes)");
        var text = string.Join('\n', entries);
        return new ToolExecuteResult(text.Length == 0 ? "(empty directory)" : $"{path} is a directory:\n{text}", false);
    }
}
