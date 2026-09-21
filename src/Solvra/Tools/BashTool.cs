#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class BashTool : ToolBase
{
    private readonly SandboxManager _sandbox;

    public BashTool(SandboxManager sandbox)
    {
        _sandbox = sandbox;
    }

    public override string Name => "bash";

    public override string Description =>
        "Run a shell command (bash -c) in the working directory and return stdout+stderr and the exit code. " +
        "Default timeout 120000 ms, max 600000. Long output is truncated (head and tail kept, full text saved to a file). " +
        "Do not cat large files; use file_read with offset/limit or grep. " +
        "Set background=true for servers or watchers: the command is started detached and its PID and log file are returned. " +
        "stdin is closed, so interactive commands fail instead of waiting.";

    public override PermissionLevel PermissionLevel => PermissionLevel.Execute;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "The shell command to execute" },
            timeout_ms = new { type = "integer", description = "Timeout in milliseconds (default 120000, max 600000)" },
            cwd = new { type = "string", description = "Working directory (optional, defaults to the project directory)" },
            background = new { type = "boolean", description = "Start detached and return immediately with PID and log path" }
        },
        required = new[] { "command" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var command = GetString(input, "command");
        if (string.IsNullOrWhiteSpace(command))
            return new ToolExecuteResult("Error: command is required", true);

        var cwd = GetOptionalString(input, "cwd") is { Length: > 0 } c
            ? (Path.IsPathRooted(c) ? c : Path.Combine(context.Cwd, c))
            : context.Cwd;
        var timeout = GetOptionalInt(input, "timeout_ms");
        var background = input.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.True;

        if (background)
            return await StartBackgroundAsync(command, cwd, context, ct);

        var result = await _sandbox.ExecAsync(command, cwd, context.Env, ct, timeout);

        if (result.Blocked)
            return new ToolExecuteResult($"[Blocked] {result.BlockReason}. The command was not run.", true);

        var output = result.Stdout;
        if (!string.IsNullOrEmpty(result.Stderr))
            output += (string.IsNullOrEmpty(output) ? "" : "\n") + result.Stderr;

        if (result.TimedOut)
        {
            var limit = Math.Clamp(timeout ?? _sandbox.Config.TimeoutMs, 1, SandboxConfig.MaxTimeoutMs);
            return new ToolExecuteResult(
                ToolOutput.Limit(output, "bash") +
                $"\n[Command timed out after {limit} ms and was killed. Raise timeout_ms (max {SandboxConfig.MaxTimeoutMs}) or use background=true.]",
                true);
        }

        output = ToolOutput.Limit(output, "bash");
        if (result.ExitCode != 0)
            output += (output.Length > 0 ? "\n" : "") + $"[exit code {result.ExitCode}]";
        else if (output.Length == 0)
            output = "(no output)";

        return new ToolExecuteResult(output, result.ExitCode != 0);
    }

    private async Task<ToolExecuteResult> StartBackgroundAsync(string command, string cwd, ToolExecutionContext context, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "solvra-bg");
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..28] + ".log");
        var quoted = "'" + command.Replace("'", "'\\''") + "'";
        var wrapper = $"nohup bash -c {quoted} > '{log}' 2>&1 < /dev/null & echo $!";

        // Screen the real command before wrapping it.
        if (_sandbox.Config.BlockDangerous && new DangerousCommandDetector().Detect(command) is { Dangerous: true } d)
            return new ToolExecuteResult($"[Blocked] {d.Reason}. The command was not run.", true);

        var result = await _sandbox.ExecAsync(wrapper, cwd, context.Env, ct, 10_000);
        if (result.Blocked)
            return new ToolExecuteResult($"[Blocked] {result.BlockReason}. The command was not run.", true);

        var pid = result.Stdout.Trim();
        return new ToolExecuteResult(
            $"Started in background. PID {pid}. Output: {log}\n" +
            $"Check it with `tail -n 50 {log}`; stop it with `kill {pid}`.",
            result.ExitCode != 0);
    }
}
