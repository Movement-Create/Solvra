#nullable enable

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Solvra.Security;

namespace Solvra.Tools;

/// <summary>
/// Records a judgment point: the question the agent faced, the options it considered, the option it
/// chose, how sure it is and which tool calls are its evidence. It changes nothing in the project; it
/// exists so the session log and audit trail carry decisions as structured records instead of prose.
/// </summary>
public class DecideTool : ToolBase
{
    internal static readonly JsonSerializerOptions RecordJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public override string Name => "decide";

    public override string Description =>
        "Record a decision point before acting on it: the question, the options considered, the option chosen, " +
        "your confidence (0-1) and the ids of the tool calls that are your evidence. Use it before choosing " +
        "between approaches or when you are not sure. It returns the record unchanged and has no other effect.";

    // Harness-internal record only (no user files, no network), so it needs no approval.
    public override PermissionLevel PermissionLevel => PermissionLevel.Read;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            question = new { type = "string", description = "What had to be decided" },
            options = new { type = "array", items = new { type = "string" }, description = "Options considered, in your own words" },
            choice = new { type = "string", description = "The option you are going with" },
            confidence = new { type = "number", minimum = 0, maximum = 1, description = "How sure you are, 0 (guess) to 1 (certain)" },
            evidence = new { type = "array", items = new { type = "string" }, description = "Tool call ids whose results support the choice" },
            rationale = new { type = "string", description = "One or two sentences on why" }
        },
        required = new[] { "question", "choice" }
    });

    public override Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var question = GetString(input, "question").Trim();
        var choice = GetString(input, "choice").Trim();
        if (question.Length == 0) return Task.FromResult(new ToolExecuteResult("Error: question is required", true));
        if (choice.Length == 0) return Task.FromResult(new ToolExecuteResult("Error: choice is required", true));

        double? confidence = null;
        if (input.TryGetProperty("confidence", out var c))
        {
            if (c.ValueKind != JsonValueKind.Number || !c.TryGetDouble(out var d) || d < 0 || d > 1)
                return Task.FromResult(new ToolExecuteResult("Error: confidence must be a number between 0 and 1", true));
            confidence = d;
        }

        var record = new
        {
            question,
            options = GetStringArray(input, "options") is { Length: > 0 } o ? o : null,
            choice,
            confidence,
            evidence = GetStringArray(input, "evidence") is { Length: > 0 } e ? e : null,
            rationale = GetOptionalString(input, "rationale")
        };
        return Task.FromResult(new ToolExecuteResult(JsonSerializer.Serialize(record, RecordJson), false));
    }
}
