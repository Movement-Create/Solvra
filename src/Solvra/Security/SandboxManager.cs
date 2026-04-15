#nullable enable

using System.Diagnostics;
using System.Text;

namespace Solvra.Security;

public record SandboxConfig(
    bool Enabled = false,
    bool BlockDangerous = true,
    int TimeoutMs = 30_000,
    int MaxOutputBytes = 1_048_576,
    string? AllowedDirectory = null,
    string? RootDir = null,
    string[]? EnvAllowlist = null,
    int? Uid = null,
    int? Gid = null)
{
    public static readonly string[] DefaultEnvAllowlist =
    [
        "PATH", "HOME", "USER", "SHELL", "LANG", "LC_ALL", "TERM",
        "NODE_ENV", "NPM_CONFIG_PREFIX", "NVM_DIR", "TZ"
    ];

    public string EffectiveRootDir => RootDir ?? Directory.GetCurrentDirectory();
    public string[] EffectiveEnvAllowlist => EnvAllowlist ?? DefaultEnvAllowlist;
}

public record SandboxExecResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    bool Blocked = false,
    string? BlockReason = null,
    bool TimedOut = false);

public class SandboxManager
{
    private SandboxConfig _config;
    private readonly DangerousCommandDetector _detector = new();

    public SandboxManager(SandboxConfig? config = null)
    {
        _config = config ?? new SandboxConfig();
    }

    public void Configure(SandboxConfig config)
    {
        _config = config;
    }

    public async Task<SandboxExecResult> ExecAsync(
        string command,
        string? cwd = null,
        Dictionary<string, string>? extraEnv = null,
        CancellationToken ct = default)
    {
        // Pre-screen dangerous commands
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

        // Validate working directory
        var workingDir = cwd ?? Directory.GetCurrentDirectory();
        if (_config.AllowedDirectory != null)
        {
            var resolvedCwd = Path.GetFullPath(workingDir);
            var resolvedAllowed = Path.GetFullPath(_config.AllowedDirectory);
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

        return await RawExecAsync(command, workingDir, extraEnv, ct);
    }

    private async Task<SandboxExecResult> RawExecAsync(
        string command,
        string cwd,
        Dictionary<string, string>? extraEnv,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        if (extraEnv != null)
        {
            foreach (var (key, value) in extraEnv)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var timedOut = false;

        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_config.TimeoutMs);

        try
        {
            var stdoutTask = ReadStreamAsync(process.StandardOutput, stdout, _config.MaxOutputBytes, timeoutCts.Token);
            var stderrTask = ReadStreamAsync(process.StandardError, stderr, _config.MaxOutputBytes, timeoutCts.Token);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        return new SandboxExecResult(
            Stdout: stdout.ToString(),
            Stderr: stderr.ToString(),
            ExitCode: timedOut ? -1 : process.ExitCode,
            TimedOut: timedOut);
    }

    private static async Task ReadStreamAsync(
        StreamReader reader, StringBuilder sb, int maxBytes, CancellationToken ct)
    {
        var buffer = new char[4096];
        var totalRead = 0;

        while (!ct.IsCancellationRequested)
        {
            var read = await reader.ReadAsync(buffer, ct);
            if (read == 0) break;

            var remaining = maxBytes - totalRead;
            if (remaining <= 0) break;

            var toAppend = Math.Min(read, remaining);
            sb.Append(buffer, 0, toAppend);
            totalRead += toAppend;
        }

        if (totalRead >= maxBytes)
        {
            sb.Append("\n[Output truncated at max bytes limit]");
        }
    }

}
