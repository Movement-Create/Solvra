namespace Solvra.Config;

/// <summary>
/// Where Solvra keeps its own state. Defaults are relative to the working directory (the
/// original behaviour); embedders that run Solvra inside a user's repository set
/// SOLVRA_SESSIONS_DIR / SOLVRA_MEMORY_DIR / SOLVRA_LOGS_DIR so nothing is written there.
/// </summary>
public static class SolvraPaths
{
    public static string SessionsDir => Env("SOLVRA_SESSIONS_DIR") ?? "sessions";
    public static string LogsDir => Env("SOLVRA_LOGS_DIR") ?? "logs";

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
