#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

/// <summary>
/// Records the result of a check the agent ran (tests, build, linter, a manual comparison): which check,
/// whether it passed, which attempt this was. It runs nothing itself; the check is done with bash or
/// code_run first. The run's end record summarises these so verification is visible without reading prose.
/// </summary>
public class VerifyTool : ToolBase
{
    public override string Name => "verify";

    public override string Description =>
        "Record the outcome of a check you just ran (tests, build, linter, manual comparison): the check's name, " +
        "whether it passed, the command or tool call id that ran it, and the attempt number if you retried. " +
        "It runs nothing itself and returns the record unchanged.";

    public override PermissionLevel PermissionLevel => PermissionLevel.Read;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            check = new { type = "string", description = "Name of the check, e.g. 'pytest', 'dotnet build', 'output matches spec'" },
            passed = new { type = "boolean", description = "Did the check pass" },
            command_or_call_id = new { type = "string", description = "The command that ran it, or the tool call id of that run" },
            attempt = new { type = "integer", minimum = 1, description = "1 for the first try, 2 if you fixed something and re-ran, and so on" },
            note = new { type = "string", description = "What failed, or what was fixed between attempts" }
        },
        required = new[] { "check", "passed" }
    });

    public override Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var check = GetString(input, "check").Trim();
        if (check.Length == 0) return Task.FromResult(new ToolExecuteResult("Error: check is required", true));
        if (!input.TryGetProperty("passed", out var p) || p.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Task.FromResult(new ToolExecuteResult("Error: passed must be true or false", true));

        var attempt = GetInt(input, "attempt", 1);
        if (attempt < 1) return Task.FromResult(new ToolExecuteResult("Error: attempt must be 1 or more", true));

        var record = new
        {
            check,
            passed = p.GetBoolean(),
            attempt,
            command_or_call_id = GetOptionalString(input, "command_or_call_id"),
            note = GetOptionalString(input, "note")
        };
        return Task.FromResult(new ToolExecuteResult(JsonSerializer.Serialize(record, DecideTool.RecordJson), false));
    }
}
