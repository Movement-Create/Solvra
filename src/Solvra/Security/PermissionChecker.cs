#nullable enable

using Solvra.Tools;

namespace Solvra.Security;

public class PermissionChecker
{
    private static readonly Dictionary<PermissionMode, PermissionLevel> ModeThresholds = new()
    {
        [PermissionMode.Default] = PermissionLevel.Execute,
        [PermissionMode.Auto] = PermissionLevel.Agent,
        [PermissionMode.Plan] = PermissionLevel.Agent,
        [PermissionMode.BypassPermissions] = PermissionLevel.Agent,
    };

    public async Task<bool> CheckPermissionAsync(
        ITool tool,
        PermissionMode mode,
        Func<ITool, Task<bool>>? promptCallback = null)
    {
        if (mode == PermissionMode.BypassPermissions) return true;
        if (mode == PermissionMode.Plan) return true;

        var threshold = ModeThresholds[mode];
        var toolRank = (int)tool.PermissionLevel;
        var thresholdRank = (int)threshold;

        // Below threshold: auto-allow
        if (toolRank < thresholdRank) return true;

        // Auto mode: allow everything
        if (mode == PermissionMode.Auto) return true;

        // Default mode at/above threshold: ask
        if (promptCallback != null)
        {
            return await promptCallback(tool);
        }

        // No callback in default mode → allow (fail-open)
        return true;
    }
}
