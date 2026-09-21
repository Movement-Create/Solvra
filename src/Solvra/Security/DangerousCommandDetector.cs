#nullable enable

using System.Text.RegularExpressions;

namespace Solvra.Security;

public record DangerousCommandResult(bool Dangerous, string? Reason);

/// <summary>
/// Best-effort screen for obviously destructive shell commands. It is a guard rail against
/// model mistakes, not a security boundary: a determined command can always be obfuscated.
/// Patterns aim at commands that destroy data outside the task (home dir, system paths, git
/// history, remote branches) while leaving ordinary build/test commands alone.
/// </summary>
public class DangerousCommandDetector
{
    // Start of a simple command: beginning, or after ; & | ( ` $( and an optional sudo/env prefix.
    private const string Cmd = @"(?:^|[;&|(`]\s*|\$\(\s*)(?:sudo\s+(?:-\S+\s+)*)?(?:env\s+(?:\S+=\S*\s+)*)?";

    private static readonly List<(Regex Pattern, string Reason)> DirectPatterns = new()
    {
        // Disk / filesystem destruction
        (new Regex(@">\s*/dev/(sd[a-z]|nvme\d|vd[a-z]|xvd[a-z]|disk\d)", RegexOptions.Compiled), "Direct write to disk device"),
        (new Regex(@"\bdd\s+.*\bof=/dev/(sd|nvme|vd|xvd|disk|hd)", RegexOptions.Compiled), "dd to disk device"),
        (new Regex(Cmd + @"mkfs(\.\w+)?\b", RegexOptions.Compiled), "Filesystem format"),
        (new Regex(Cmd + @"(fdisk|sfdisk|parted|wipefs)\s", RegexOptions.Compiled), "Disk partition modification"),
        (new Regex(@"\bfind\s+(/|~|\$HOME)(\s|$)[^;|&]*(-delete\b|-exec\s+rm\b)", RegexOptions.Compiled), "find -delete over / or home"),
        (new Regex(Cmd + @"chmod\s+(-R\s+|--recursive\s+)?(0?777|a\+rwx)\s+(/|~|\$HOME)", RegexOptions.Compiled), "World-writable permissions on system or home path"),
        (new Regex(Cmd + @"chown\s+(-R\s+)?\S+\s+/(etc|usr|bin|boot|lib|sbin)\b", RegexOptions.Compiled), "Ownership change on system path"),

        // Fork bomb
        (new Regex(@":\(\)\s*\{.*\|.*&\s*\}\s*;", RegexOptions.Compiled), "Fork bomb"),

        // Remote code execution
        (new Regex(@"\b(curl|wget)\s[^|;]*\|\s*(sudo\s+)?(ba|z|da|k)?sh\b", RegexOptions.Compiled), "Download piped to shell"),
        (new Regex(@"\b(curl|wget)\s[^|;]*\|\s*(sudo\s+)?(python\d?|perl|ruby|node)\b", RegexOptions.Compiled), "Download piped to interpreter"),
        (new Regex(@"\b(ba|z)?sh\s+(-c\s+)?[""']?\$\(\s*(curl|wget)\b", RegexOptions.Compiled), "Shell running downloaded code"),
        (new Regex(@"\b(ba|z)?sh\s+<\(\s*(curl|wget)\b", RegexOptions.Compiled), "Shell running downloaded code"),
        (new Regex(@"\beval\s+[""']?\$\(\s*(curl|wget)\b", RegexOptions.Compiled), "Eval of downloaded code"),
        (new Regex(@"\$\([^)]*\)\s*\|\s*(sudo\s+)?(ba|z)?sh\b", RegexOptions.Compiled), "Command substitution piped to shell"),
        (new Regex(@"\b(curl|wget)\s[^;]*(>|-o|-O)\s*(?<f>/tmp/\S+)[^;]*;\s*((ba)?sh|chmod\s+\+x)\s+\k<f>", RegexOptions.Compiled), "Download and execute"),

        // Git history / remote destruction
        (new Regex(Cmd + @"git\s+(-C\s+\S+\s+)?push\s+([^;&|]*\s)?(--force(-with-lease)?|-f)\b", RegexOptions.Compiled), "git push --force (rewrites remote history)"),
        (new Regex(Cmd + @"git\s+(-C\s+\S+\s+)?reset\s+([^;&|]*\s)?--hard\b", RegexOptions.Compiled), "git reset --hard (discards uncommitted work)"),
        (new Regex(Cmd + @"git\s+(-C\s+\S+\s+)?clean\s+([^;&|]*\s)?-[a-zA-Z]*(f[a-zA-Z]*d|d[a-zA-Z]*f)", RegexOptions.Compiled), "git clean -fd (deletes untracked files)"),

        // Accounts
        (new Regex(Cmd + @"(passwd|chpasswd|usermod|userdel)\b", RegexOptions.Compiled), "Account modification"),

        // Network backdoors
        (new Regex(@"\bnc(at)?\s+(-[a-zA-Z]*\s+)*-[a-zA-Z]*[le][a-zA-Z]*(\s|$)", RegexOptions.Compiled), "Netcat listener / exec"),
        (new Regex(@"/dev/(tcp|udp)/", RegexOptions.Compiled), "Bash network device"),
    };

    private static readonly Regex Base64ExecPattern = new(
        @"base64\s+(-d|--decode)\b[^;]*\|\s*(sudo\s+)?(ba|z)?sh\b",
        RegexOptions.Compiled);

    private static readonly Regex ScriptExecPattern = new(
        @"python[23]?\s+-c\s+.*(os\.system|subprocess)\S*\(\s*.*rm\s+-[a-zA-Z]*r[a-zA-Z]*\s+(/|~)|shutil\.rmtree\(\s*['""](/|~)",
        RegexOptions.Compiled);

    private static readonly Regex RmCommand = new(Cmd + @"rm\s+(?<args>[^;&|`)]*)", RegexOptions.Compiled);

    private static readonly HashSet<string> CatastrophicTargets = new(StringComparer.Ordinal)
    {
        "/", "/*", "~", "~/", "~/*", "$HOME", "$HOME/", "$HOME/*", "${HOME}", "${HOME}/",
        ".", "./", "..", "../", "*", ".*", "./*", "../*",
    };

    private static readonly Regex SystemPath = new(
        @"^/(bin|boot|dev|etc|home|lib|lib32|lib64|opt|proc|root|sbin|srv|sys|usr|var|mnt|media|snap)(/\*?)?$",
        RegexOptions.Compiled);

    public DangerousCommandResult Detect(string command)
    {
        var normalized = Regex.Replace(command, @"\s+", " ").Trim();

        foreach (var (pattern, reason) in DirectPatterns)
        {
            if (pattern.IsMatch(normalized))
                return new DangerousCommandResult(true, reason);
        }

        var rm = CheckRm(normalized);
        if (rm != null) return new DangerousCommandResult(true, rm);

        if (Base64ExecPattern.IsMatch(normalized))
            return new DangerousCommandResult(true, "Base64-encoded command execution");

        if (ScriptExecPattern.IsMatch(normalized))
            return new DangerousCommandResult(true, "Script interpreter deleting system or home paths");

        return new DangerousCommandResult(false, null);
    }

    /// <summary>Recursive rm aimed at /, home, the whole working tree, or a system directory.</summary>
    private static string? CheckRm(string normalized)
    {
        foreach (Match m in RmCommand.Matches(normalized))
        {
            var tokens = m.Groups["args"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var recursive = false;
            var targets = new List<string>();
            var endOfOptions = false;
            foreach (var t in tokens)
            {
                if (!endOfOptions && t == "--") { endOfOptions = true; continue; }
                if (!endOfOptions && t.StartsWith("--"))
                {
                    if (t == "--recursive") recursive = true;
                    if (t == "--no-preserve-root") return "rm --no-preserve-root";
                    continue;
                }
                if (!endOfOptions && t.StartsWith('-') && t.Length > 1)
                {
                    if (t.IndexOfAny(['r', 'R']) >= 0) recursive = true;
                    continue;
                }
                targets.Add(t.Trim('\'', '"'));
            }

            if (!recursive) continue;
            foreach (var target in targets)
            {
                if (CatastrophicTargets.Contains(target)) return $"Recursive delete of '{target}'";
                if (SystemPath.IsMatch(target)) return $"Recursive delete of system path '{target}'";
            }
        }
        return null;
    }
}
