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
    SessionInfo Session);
