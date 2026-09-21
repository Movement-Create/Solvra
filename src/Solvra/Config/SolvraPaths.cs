namespace Solvra.Config;

/// <summary>
/// Where Solvra keeps its own state. Defaults live under $XDG_DATA_HOME/solvra
/// (~/.local/share/solvra) so running Solvra inside a repository never litters it with
/// logs/, sessions/ or memory/ directories (models then found and read those files).
/// SOLVRA_SESSIONS_DIR / SOLVRA_MEMORY_DIR / SOLVRA_LOGS_DIR override each location, and
/// SOLVRA_HOME moves all of them at once.
/// </summary>
public static class SolvraPaths
{
    public static string DataHome
    {
        get
        {
            var home = Env("SOLVRA_HOME");
            if (home != null) return home;
            var xdg = Env("XDG_DATA_HOME");
            var baseDir = xdg ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(baseDir, "solvra");
        }
    }

    public static string SessionsDir => Env("SOLVRA_SESSIONS_DIR") ?? Path.Combine(DataHome, "sessions");
    public static string LogsDir => Env("SOLVRA_LOGS_DIR") ?? Path.Combine(DataHome, "logs");
    public static string MemoryDir => Env("SOLVRA_MEMORY_DIR") ?? Path.Combine(DataHome, "memory");

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
