#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public interface IToolRegistry
{
    IDisposable RegisterTool(ITool tool);
    bool Unregister(string name);
    void RegisterBuiltins(Security.SandboxManager sandbox);
    IReadOnlyList<Models.ToolDefinition> GetToolDefinitions();
    ITool? GetTool(string name);
    Task<ToolExecuteResult> ExecuteToolAsync(string name, JsonElement input, ToolExecutionContext context, CancellationToken ct = default);

    /// <summary>
    /// Execute a tool with integrated permission checking.
    /// </summary>
    Task<ToolExecuteResult> ExecuteToolAsync(
        string name,
        JsonElement input,
        ToolExecutionContext context,
        PermissionMode permissionMode,
        Func<ITool, Task<bool>>? permissionCallback = null,
        CancellationToken ct = default);
}
