#nullable enable

namespace Solvra.Tools;

public record SessionInfo(
    string Id,
    string PermissionModeStr,
    List<string> AllowedTools,
    List<string> DisallowedTools);

public record ToolExecutionContext(
    string SessionId,
    string Cwd,
    bool PlanMode,
    Dictionary<string, string> Env,
    SessionInfo Session)
{
    /// <summary>Nesting depth of the agent running this tool (0 = top level).</summary>
    public int SubagentDepth { get; init; }

    /// <summary>The parent run's permission prompt, so subagents ask the same user.</summary>
    public Func<Models.ToolCall, Task<bool>>? PermissionRequest { get; init; }

    /// <summary>The parent run's model and provider, inherited by subagents.</summary>
    public string? Model { get; init; }
    public string? Provider { get; init; }
}
