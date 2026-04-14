#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    PermissionLevel PermissionLevel { get; }
    JsonElement GetInputSchema();
    Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default);
}
