#nullable enable

using System.Text.Json;
using Solvra.Security;
using Solvra.Memory;

namespace Solvra.Tools;

public class MemoryRecallTool : ToolBase
{
    public override string Name => "memory_recall";
    public override string Description => "Search memory for relevant facts and lessons.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Read;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Search query" }
        },
        required = new[] { "query" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var query = GetString(input, "query");
        if (string.IsNullOrWhiteSpace(query))
            return new ToolExecuteResult("Error: query is required", true);

        var memoryDir = Path.Combine(context.Cwd, "memory");
        var manager = new MemoryManager(memoryDir);
        var results = await manager.SearchAsync(query);

        if (string.IsNullOrEmpty(results))
            return new ToolExecuteResult("No relevant memories found.", false);

        return new ToolExecuteResult(results, false);
    }
}
