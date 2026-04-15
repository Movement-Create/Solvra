#nullable enable

using System.Text.Json;

namespace Solvra.Tools;

public interface IToolRegistry
{
    void RegisterTool(ITool tool);
    void RegisterBuiltins(Security.SandboxManager sandbox);
    IReadOnlyList<Models.ToolDefinition> GetToolDefinitions();
    Task<ToolExecuteResult> ExecuteToolAsync(string name, JsonElement input, ToolExecutionContext context, CancellationToken ct = default);
}
