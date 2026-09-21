#nullable enable

using Solvra.Tools;

namespace Solvra.Security;

/// <summary>
/// Permission policy per mode:
/// - Auto / BypassPermissions: everything runs.
/// - Plan: read-only. Read and Network tools run; Write, Execute and Agent tools are refused so the
///   model has to describe its plan instead of carrying it out.
/// - Default: Read and Network tools run; Write, Execute and Agent tools need the prompt callback
///   to say yes. With no callback (nobody to ask) they are refused: fail closed, not open.
/// </summary>
public class PermissionChecker
{
    public static bool AllowedInPlanMode(ITool tool) =>
        tool.PermissionLevel is PermissionLevel.Read or PermissionLevel.Network;

    public async Task<bool> CheckPermissionAsync(
        ITool tool,
        PermissionMode mode,
        Func<ITool, Task<bool>>? promptCallback = null)
    {
        switch (mode)
        {
            case PermissionMode.BypassPermissions:
            case PermissionMode.Auto:
                return true;
            case PermissionMode.Plan:
                return AllowedInPlanMode(tool);
        }

        if (tool.PermissionLevel is PermissionLevel.Read or PermissionLevel.Network)
            return true;

        return promptCallback != null && await promptCallback(tool);
    }
}
