namespace Solvra.Models;

public enum EffortLevel
{
    Low,
    Medium,
    High,
    Max
}

public static class EffortLevelExtensions
{
    private static readonly Dictionary<EffortLevel, int> TokenBudgets = new()
    {
        [EffortLevel.Low] = 1024,
        [EffortLevel.Medium] = 4096,
        [EffortLevel.High] = 16384,
        [EffortLevel.Max] = 65536
    };

    public static int GetTokenBudget(this EffortLevel level) =>
        TokenBudgets.GetValueOrDefault(level, 4096);

    public static EffortLevel Parse(string value) => value.ToLowerInvariant() switch
    {
        "low" => EffortLevel.Low,
        "medium" => EffortLevel.Medium,
        "high" => EffortLevel.High,
        "max" => EffortLevel.Max,
        _ => EffortLevel.Medium
    };

    public static string ToWireString(this EffortLevel level) => level switch
    {
        EffortLevel.Low => "low",
        EffortLevel.Medium => "medium",
        EffortLevel.High => "high",
        EffortLevel.Max => "max",
        _ => "medium"
    };
}
