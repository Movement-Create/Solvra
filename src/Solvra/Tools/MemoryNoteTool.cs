#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class MemoryNoteTool : ToolBase
{
    public override string Name => "memory_note";
    public override string Description => "Save a memory note (fact or lesson) for future recall.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Write;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            content = new { type = "string", description = "Memory content to save" },
            tags = new { type = "array", items = new { type = "string" }, description = "Tags for categorization" },
            kind = new { type = "string", description = "Type: fact or lesson (default: lesson)" }
        },
        required = new[] { "content" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var content = GetString(input, "content");
        if (string.IsNullOrWhiteSpace(content))
            return new ToolExecuteResult("Error: content is required", true);

        var tags = GetStringArray(input, "tags");
        var kind = GetOptionalString(input, "kind") ?? "lesson";

        var memoryDir = Path.Combine(context.Cwd, "memory");
        Directory.CreateDirectory(memoryDir);

        var filename = kind == "fact" ? "facts.md" : "lessons.md";
        var filePath = Path.Combine(memoryDir, filename);

        var tagsStr = tags.Length > 0 ? $" [{string.Join(", ", tags)}]" : "";
        var entry = $"\n## {DateTime.UtcNow:yyyy-MM-dd}{tagsStr}\n{content}\n";

        await File.AppendAllTextAsync(filePath, entry, ct);

        return new ToolExecuteResult($"Memory saved to {filename}", false);
    }
}
