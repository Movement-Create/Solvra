namespace Solvra.Models;

public enum EffortLevel
{
    Low,
    Medium,
    High,
    ExtraHigh,

    // Source-compatible alias for configurations and integrations that used the old name.
    Max = ExtraHigh
}

public static class EffortLevelExtensions
{
    private static readonly Dictionary<EffortLevel, int> TokenBudgets = new()
    {
        [EffortLevel.Low] = 1024,
        [EffortLevel.Medium] = 4096,
        [EffortLevel.High] = 16384,
        [EffortLevel.ExtraHigh] = 65536
    };

    public static int GetTokenBudget(this EffortLevel level) =>
        TokenBudgets.GetValueOrDefault(level, 4096);

    public static EffortLevel Parse(string value) => TryParse(value, out var level)
        ? level
        : throw new ArgumentException($"Invalid effort '{value}'. Expected low, medium, high, or xhigh.", nameof(value));

    public static bool TryParse(string? value, out EffortLevel level)
    {
        level = value?.Trim().ToLowerInvariant() switch
        {
            "low" => EffortLevel.Low,
            "medium" => EffortLevel.Medium,
            "high" => EffortLevel.High,
            "xhigh" or "extra-high" or "extra_high" or "max" => EffortLevel.ExtraHigh,
            _ => (EffortLevel)(-1)
        };
        return Enum.IsDefined(level);
    }

    public static string ToWireString(this EffortLevel level) => level switch
    {
        EffortLevel.Low => "low",
        EffortLevel.Medium => "medium",
        EffortLevel.High => "high",
        EffortLevel.ExtraHigh => "xhigh",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown effort level.")
    };
}
