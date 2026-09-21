#nullable enable

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Solvra.Security;

public record SandboxConfig(
    bool Enabled = false,
    bool BlockDangerous = true,
    int TimeoutMs = 120_000,
    int MaxOutputBytes = 8 * 1024 * 1024,
    string? AllowedDirectory = null,
    string? RootDir = null,
    string[]? EnvAllowlist = null,
    int? Uid = null,
    int? Gid = null)
{
    /// <summary>Variables always kept when a strict <see cref="EnvAllowlist"/> is configured.</summary>
    public static readonly string[] DefaultEnvAllowlist =
    [
        "PATH", "HOME", "USER", "SHELL", "LANG", "LC_ALL", "TERM",
        "NODE_ENV", "NPM_CONFIG_PREFIX", "NVM_DIR", "TZ"
    ];

    /// <summary>Hard ceiling for a single command's timeout (10 minutes).</summary>
    public const int MaxTimeoutMs = 600_000;

    public string EffectiveRootDir => RootDir ?? Directory.GetCurrentDirectory();
    public string[] EffectiveEnvAllowlist => EnvAllowlist ?? DefaultEnvAllowlist;
}

public record SandboxExecResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    bool Blocked = false,
    string? BlockReason = null,
    bool TimedOut = false,
    bool Truncated = false);

public class SandboxManager
{
    private SandboxConfig _config;
    private readonly DangerousCommandDetector _detector = new();

    /// <summary>
    /// Names that look like credentials. Tool processes run model-chosen commands, so they must not
    /// inherit the harness's API keys or webhook secret (a single `env` would leak them into the
    /// transcript, or a prompt-injected command could send them elsewhere).
    /// </summary>
    private static readonly Regex SecretName = new(
        @"(KEY|TOKEN|SECRET|PASSWORD|PASSWD|PASSPHRASE|CREDENTIAL|COOKIE|_AUTH|AUTH_|PRIVATE)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SecretExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPENAI_BASE_URL", "ANTHROPIC_BASE_URL", "DATABASE_URL", "SOLVRA_OPENCODE_SESSION",
    };

    public SandboxManager(SandboxConfig? config = null)
    {
        _config = config ?? new SandboxConfig();
    }

    public SandboxConfig Config => _config;

    public void Configure(SandboxConfig config)
    {
        _config = config;
    }

    public async Task<SandboxExecResult> ExecAsync(
        string command,
        string? cwd = null,
        Dictionary<string, string>? extraEnv = null,
        CancellationToken ct = default,
        int? timeoutMs = null)
    {
        if (_config.BlockDangerous)
        {
            var check = _detector.Detect(command);
            if (check.Dangerous)
            {
                return new SandboxExecResult(
                    Stdout: "",
                    Stderr: $"[Sandbox] Blocked: {check.Reason}",
                    ExitCode: 1,
                    Blocked: true,
                    BlockReason: check.Reason);
            }
        }

        var workingDir = cwd ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(workingDir))
        {
            return new SandboxExecResult("", $"[Sandbox] Working directory does not exist: {workingDir}", 1,
                Blocked: true, BlockReason: "Missing working directory");
        }

        if (_config.AllowedDirectory != null)
        {
            var resolvedCwd = Path.GetFullPath(workingDir).TrimEnd('/') + "/";
            var resolvedAllowed = Path.GetFullPath(_config.AllowedDirectory).TrimEnd('/') + "/";
            if (!resolvedCwd.StartsWith(resolvedAllowed, StringComparison.Ordinal))
            {
                return new SandboxExecResult(
                    Stdout: "",
                    Stderr: $"[Sandbox] Directory '{workingDir}' is outside allowed directory '{_config.AllowedDirectory}'",
                    ExitCode: 1,
                    Blocked: true,
                    BlockReason: "Directory outside allowed scope");
            }
        }

        var timeout = Math.Clamp(timeoutMs ?? _config.TimeoutMs, 1, SandboxConfig.MaxTimeoutMs);
        return await RawExecAsync(command, workingDir, extraEnv, timeout, ct);
    }

    /// <summary>
    /// Build the child environment: a strict allowlist when configured, otherwise the parent
    /// environment minus anything that looks like a credential. SOLVRA_TOOL_ENV_PASSTHROUGH
    /// (comma-separated names) keeps specific variables regardless.
    /// </summary>
    internal void ApplyEnvironment(ProcessStartInfo psi, Dictionary<string, string>? extraEnv)
    {
        var passthrough = new HashSet<string>(
            (Environment.GetEnvironmentVariable("SOLVRA_TOOL_ENV_PASSTHROUGH") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        if (_config.EnvAllowlist != null)
        {
            var keep = new HashSet<string>(_config.EffectiveEnvAllowlist, StringComparer.OrdinalIgnoreCase);
            foreach (var name in psi.Environment.Keys.ToList())
            {
                if (!keep.Contains(name) && !passthrough.Contains(name))
                    psi.Environment.Remove(name);
            }
        }
        else
        {
            foreach (var name in psi.Environment.Keys.ToList())
            {
                if (passthrough.Contains(name)) continue;
                if (IsSecretName(name)) psi.Environment.Remove(name);
            }
        }

        if (extraEnv != null)
        {
            foreach (var (key, value) in extraEnv)
            {
                if (key.StartsWith("__", StringComparison.Ordinal)) continue; // internal markers
                psi.Environment[key] = value;
            }
        }
    }

    public static bool IsSecretName(string name) =>
        SecretExact.Contains(name) || (SecretName.IsMatch(name) && !name.Equals("SSH_AUTH_SOCK", StringComparison.Ordinal));

    private async Task<SandboxExecResult> RawExecAsync(
        string command,
        string cwd,
        Dictionary<string, string>? extraEnv,
        int timeoutMs,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Never share the harness's stdin: the NDJSON chat protocol arrives there, and an
            // interactive command would otherwise hang or swallow protocol lines.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        ApplyEnvironment(psi, extraEnv);

        using var process = new Process { StartInfo = psi };
        var stdout = new CappedBuffer(_config.MaxOutputBytes);
        var stderr = new CappedBuffer(_config.MaxOutputBytes);
        var timedOut = false;

        process.Start();
        try { process.StandardInput.Close(); } catch { /* already gone */ }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        // Readers always drain to EOF (discarding past the cap), so a chatty command can never
        // block on a full pipe; they stop only on timeout/cancel.
        var stdoutTask = ReadStreamAsync(process.StandardOutput, stdout, timeoutCts.Token);
        var stderrTask = ReadStreamAsync(process.StandardError, stderr, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            // A background child (e.g. `server &`) can hold the pipes open after the shell exits;
            // give the readers a moment, then stop waiting.
            var readers = Task.WhenAll(stdoutTask, stderrTask);
            if (await Task.WhenAny(readers, Task.Delay(2000, CancellationToken.None)) != readers)
                timeoutCts.Cancel();
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                KillTree(process);
                throw;
            }
            timedOut = true;
            KillTree(process);
        }

        try { await Task.WhenAll(stdoutTask, stderrTask); } catch { /* cancelled readers */ }

        return new SandboxExecResult(
            Stdout: stdout.ToString(),
            Stderr: stderr.ToString(),
            ExitCode: timedOut ? -1 : SafeExitCode(process),
            TimedOut: timedOut,
            Truncated: stdout.Truncated || stderr.Truncated);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.HasExited ? p.ExitCode : -1; } catch { return -1; }
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static async Task ReadStreamAsync(StreamReader reader, CappedBuffer sb, CancellationToken ct)
    {
        var buffer = new char[8192];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer, ct);
                if (read == 0) break;
                sb.Append(buffer, read);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Keeps the first <c>max</c> chars and counts the rest.</summary>
    private sealed class CappedBuffer(int max)
    {
        private readonly StringBuilder _sb = new();
        private readonly object _lock = new();
        private long _dropped;

        public bool Truncated => _dropped > 0;

        public void Append(char[] buffer, int count)
        {
            lock (_lock)
            {
                var room = max - _sb.Length;
                if (room > 0) _sb.Append(buffer, 0, Math.Min(room, count));
                if (count > room) _dropped += count - Math.Max(room, 0);
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return _dropped > 0
                    ? _sb + $"\n[Output truncated: {_dropped} more characters discarded]"
                    : _sb.ToString();
            }
        }
    }
}
