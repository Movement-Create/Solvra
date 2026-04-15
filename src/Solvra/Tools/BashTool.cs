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
    public override string Description => "Execute a shell command and return its output.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Execute;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "The shell command to execute" },
            timeout_ms = new { type = "integer", description = "Timeout in milliseconds (default 30000)", @default = 30000 },
            cwd = new { type = "string", description = "Working directory (optional)" }
        },
        required = new[] { "command" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var command = GetString(input, "command");
        if (string.IsNullOrWhiteSpace(command))
            return new ToolExecuteResult("Error: command is required", true);

        var cwd = GetOptionalString(input, "cwd") ?? context.Cwd;

        var result = await _sandbox.ExecAsync(command, cwd, context.Env, ct);

        if (result.Blocked)
            return new ToolExecuteResult($"[Blocked] {result.BlockReason}", true);

        if (result.TimedOut)
            return new ToolExecuteResult($"Command timed out\n{result.Stdout}{result.Stderr}", true);

        var output = result.Stdout;
        if (!string.IsNullOrEmpty(result.Stderr))
            output += (string.IsNullOrEmpty(output) ? "" : "\n") + result.Stderr;

        return new ToolExecuteResult(output, result.ExitCode != 0);
    }
}
