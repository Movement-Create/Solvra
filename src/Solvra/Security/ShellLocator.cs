#nullable enable

namespace Solvra.Security;

/// <summary>
/// Finds the programs Solvra launches, on macOS, Linux and Windows. The resolution functions take
/// the environment as parameters so they can be tested on any OS.
/// </summary>
public static class ShellLocator
{
    public const string WindowsBashHelp =
        "Running commands needs bash. On Windows, install Git for Windows (https://git-scm.com/download/win; " +
        "it includes Git Bash), or set SOLVRA_BASH to a bash.exe, then try again.";

    /// <summary>bash for the bash tool and shell hooks; null when there is none (Windows without Git Bash).</summary>
    public static string? Bash() =>
        ResolveBash(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, File.Exists);

    public static string? ResolveBash(bool isWindows, Func<string, string?> env, Func<string, bool> exists)
    {
        var custom = env("SOLVRA_BASH");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        if (!isWindows) return exists("/bin/bash") ? "/bin/bash" : "bash";

        // Git for Windows (machine-wide, per-user, 32-bit), then bash.exe on PATH. WSL's
        // C:\Windows\System32\bash.exe is skipped: it runs commands inside Linux, not here.
        foreach (var root in new[] { env("ProgramFiles"), env("ProgramW6432"), env("ProgramFiles(x86)"), Combine(env("LOCALAPPDATA"), "Programs") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "Git", "bin", "bash.exe");
            if (exists(candidate)) return candidate;
        }
        foreach (var dir in SplitPath(env("PATH"), ';'))
        {
            if (dir.Contains(@"\System32", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = Path.Combine(dir, "bash.exe");
            if (exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>How to start the Codex CLI: an executable, or cmd.exe for npm's codex.cmd shim on Windows.</summary>
    public sealed record Launch(string FileName, IReadOnlyList<string> PrefixArgs);

    public static Launch Codex() =>
        ResolveCodex(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, File.Exists,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static Launch ResolveCodex(bool isWindows, Func<string, string?> env, Func<string, bool> exists, string home)
    {
        var custom = env("SOLVRA_CODEX_BIN");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(custom)) candidates.Add(custom);
        if (isWindows)
        {
            foreach (var dir in SplitPath(env("PATH"), ';'))
            {
                candidates.Add(Path.Combine(dir, "codex.exe"));
                candidates.Add(Path.Combine(dir, "codex.cmd"));
            }
            var appData = env("APPDATA");
            if (!string.IsNullOrEmpty(appData)) candidates.Add(Path.Combine(appData, "npm", "codex.cmd"));
        }
        else
        {
            candidates.Add(Path.Combine(home, ".npm-global", "bin", "codex"));
            foreach (var dir in SplitPath(env("PATH"), ':')) candidates.Add(Path.Combine(dir, "codex"));
            candidates.AddRange(["/opt/homebrew/bin/codex", "/usr/local/bin/codex", Path.Combine(home, ".local", "bin", "codex")]);
        }
        foreach (var c in candidates)
        {
            if (!exists(c)) continue;
            // A .cmd/.bat shim cannot be started directly; run it through cmd.exe. The arguments
            // Solvra passes are fixed (e.g. "app-server"), never user text.
            if (isWindows && (c.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || c.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
                return new Launch(Combine(env("SystemRoot"), "System32", "cmd.exe") ?? "cmd.exe", ["/d", "/c", c]);
            return new Launch(c, []);
        }
        return new Launch(isWindows ? "codex.exe" : "codex", []);
    }

    private static IEnumerable<string> SplitPath(string? value, char separator) =>
        (value ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Combine(string? root, params string[] parts) =>
        string.IsNullOrEmpty(root) ? null : Path.Combine([root, .. parts]);
}
