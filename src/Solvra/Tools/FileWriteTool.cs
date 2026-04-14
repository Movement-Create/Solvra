#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class FileWriteTool : ToolBase
{
    public override string Name => "file_write";
    public override string Description => "Write content to a file, creating directories as needed.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Write;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "File path to write" },
            content = new { type = "string", description = "Content to write" }
        },
        required = new[] { "path", "content" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var path = GetString(input, "path");
        var content = GetString(input, "content");

        if (string.IsNullOrWhiteSpace(path))
            return new ToolExecuteResult("Error: path is required", true);

        if (!Path.IsPathRooted(path))
            path = Path.Combine(context.Cwd, path);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content, ct);

        return new ToolExecuteResult($"File written: {path} ({content.Length} bytes)", false);
    }
}
