#nullable enable

using System.Text.RegularExpressions;

namespace Solvra.Security;

public record DangerousCommandResult(bool Dangerous, string? Reason);

public class DangerousCommandDetector
{
    private static readonly List<(Regex Pattern, string Reason)> DirectPatterns = new()
    {
        // Filesystem destruction
        (new Regex(@"rm\s+(-[a-zA-Z]*f[a-zA-Z]*\s+|--force\s+).*/", RegexOptions.Compiled), "Forced recursive delete"),
        (new Regex(@"rm\s+(-[a-zA-Z]*r[a-zA-Z]*\s+|--recursive\s+).*/", RegexOptions.Compiled), "Recursive delete from root-adjacent path"),
        (new Regex(@">\s*/dev/sd[a-z]", RegexOptions.Compiled), "Direct write to disk device"),
        (new Regex(@"dd\s+.*of=/dev/", RegexOptions.Compiled), "dd to disk device"),
        (new Regex(@"mkfs\.", RegexOptions.Compiled), "Filesystem format"),
        (new Regex(@"fdisk\s", RegexOptions.Compiled), "Disk partition modification"),

        // Fork/resource bombs
        (new Regex(@":\(\)\s*\{.*\|.*&\s*\}\s*;", RegexOptions.Compiled), "Fork bomb"),
        (new Regex(@"while\s+true.*do.*done", RegexOptions.Compiled), "Infinite loop (review manually)"),

        // Remote code execution
        (new Regex(@"curl\s[^|]*\|\s*(bash|sh|zsh|python|perl|ruby)", RegexOptions.Compiled), "Curl pipe to interpreter"),
        (new Regex(@"wget\s[^|]*\|\s*(bash|sh|zsh|python|perl|ruby)", RegexOptions.Compiled), "Wget pipe to interpreter"),
        (new Regex(@"curl\s[^|]*>\s*/tmp/[^;]*;\s*(bash|sh|chmod)", RegexOptions.Compiled), "Download and execute pattern"),

        // Eval / injection
        (new Regex(@"eval\s+[""'`$]", RegexOptions.Compiled), "Eval with dynamic input"),
        (new Regex(@"\$\(.*\)\s*\|\s*(bash|sh)", RegexOptions.Compiled), "Command substitution piped to shell"),

        // Credential / system compromise
        (new Regex(@"passwd\s", RegexOptions.Compiled), "Password modification"),
        (new Regex(@"chmod\s+(0?777|a\+rwx)\s+/", RegexOptions.Compiled), "World-writable permissions on system path"),
        (new Regex(@"chown\s+.*/etc", RegexOptions.Compiled), "Ownership change on system config"),

        // Network exfiltration indicators
        (new Regex(@"nc\s+-[a-zA-Z]*l[a-zA-Z]*\s", RegexOptions.Compiled), "Netcat listener"),
        (new Regex(@"/dev/(tcp|udp)/", RegexOptions.Compiled), "Bash network device"),
    };

    private static readonly Regex Base64ExecPattern = new(
        @"echo\s+[A-Za-z0-9+/=]{8,}\s*\|\s*base64\s+-d\s*\|\s*(bash|sh)",
        RegexOptions.Compiled);

    private static readonly Regex ScriptExecPattern = new(
        @"python[23]?\s+-c\s+[""'].*(?:os\.system|subprocess|exec|__import__).*[""']",
        RegexOptions.Compiled);

    public DangerousCommandResult Detect(string command)
    {
        var normalized = Regex.Replace(command, @"\s+", " ").Trim();

        // Layer 1: Direct pattern matching
        foreach (var (pattern, reason) in DirectPatterns)
        {
            if (pattern.IsMatch(normalized))
            {
                return new DangerousCommandResult(true, reason);
            }
        }

        // Layer 2: Base64-encoded command execution
        if (Base64ExecPattern.IsMatch(normalized))
        {
            return new DangerousCommandResult(true, "Base64-encoded command execution");
        }

        // Layer 3: Script interpreter system calls
        if (ScriptExecPattern.IsMatch(normalized))
        {
            return new DangerousCommandResult(true, "Script interpreter system call");
        }

        return new DangerousCommandResult(false, null);
    }
}
