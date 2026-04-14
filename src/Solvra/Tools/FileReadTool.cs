#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class FileReadTool : ToolBase
{
    public override string Name => "file_read";
    public override string Description => "Read file contents with optional offset and limit.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Read;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "File path to read" },
            offset = new { type = "integer", description = "Line number to start from (0-based)" },
            limit = new { type = "integer", description = "Maximum number of lines to read" }
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

        if (!File.Exists(path))
            return new ToolExecuteResult($"Error: file not found: {path}", true);

        var offset = GetOptionalInt(input, "offset") ?? 0;
        var limit = GetOptionalInt(input, "limit");

        var lines = await File.ReadAllLinesAsync(path, ct);
        var selectedLines = lines.Skip(offset);
        if (limit.HasValue)
            selectedLines = selectedLines.Take(limit.Value);

        var numberedLines = selectedLines
            .Select((line, i) => $"{offset + i + 1}\t{line}");

        return new ToolExecuteResult(string.Join('\n', numberedLines), false);
    }
}
