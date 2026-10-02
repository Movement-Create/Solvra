#nullable enable

using System.Text.Json;
using Solvra.Models;
using Solvra.Security;

namespace Solvra.Tools;

/// <summary>What a subagent run needs from its parent.</summary>
public sealed record SubagentRequest(
    string Prompt,
    string? Model,
    EffortLevel Effort,
    string? SystemPrompt,
    int MaxTurns,
    int Depth,
    ToolExecutionContext Parent);

/// <summary>
/// Spawn a subagent. Uses lazy loading to avoid circular dependency with the agent loop.
/// The subagent inherits the parent's permission mode, approval prompt, working directory,
/// provider and tool allow/deny lists, and nesting is capped at <see cref="MaxSubagentDepth"/>.
/// </summary>
public class AgentTool : ToolBase
{
    public const int MaxSubagentDepth = 2;

    /// <summary>Runs a subagent and returns its final text. Set by Program.</summary>
    public static Func<SubagentRequest, CancellationToken, Task<string>>? RunAgentDelegate { get; set; }

    public override string Name => "agent";

    public override string Description =>
        "Delegate a self-contained sub-task (e.g. a broad search or an independent change) to a subagent with its own " +
        "context. Give it a complete prompt: it cannot see this conversation. Returns the subagent's final answer.";

    public override PermissionLevel PermissionLevel => PermissionLevel.Agent;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            prompt = new { type = "string", description = "Complete task description for the subagent" },
            model = new { type = "string", description = "Model to use (optional, defaults to the configured subagent model or current model)" },
            effort = new { type = "string", @enum = new[] { "low", "medium", "high", "xhigh" }, description = "Reasoning effort (optional, defaults to the configured subagent effort or current effort)" },
            system_prompt = new { type = "string", description = "Custom system prompt (optional)" },
            max_turns = new { type = "integer", minimum = 0, description = "Maximum turns (optional; omitted inherits the parent limit, 0 means unlimited)" }
        },
        required = new[] { "prompt" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var prompt = GetString(input, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return new ToolExecuteResult("Error: prompt is required", true);

        if (!context.SubagentsEnabled)
            return new ToolExecuteResult("Error: subagents are disabled for this run.", true);

        if (context.SubagentDepth >= MaxSubagentDepth)
            return new ToolExecuteResult($"Error: maximum subagent nesting depth ({MaxSubagentDepth}) reached. Do the work directly.", true);

        var model = GetOptionalString(input, "model");
        var effortText = GetOptionalString(input, "effort");
        if (effortText is not null && !EffortLevelExtensions.TryParse(effortText, out _))
            return new ToolExecuteResult($"Error: invalid effort '{effortText}'. Expected low, medium, high, or xhigh.", true);
        var effort = effortText is null ? context.Effort : EffortLevelExtensions.Parse(effortText);
        var systemPrompt = GetOptionalString(input, "system_prompt");
        var requestedMaxTurns = GetOptionalInt(input, "max_turns");
        var maxTurns = requestedMaxTurns ?? context.MaxTurns;
        if (maxTurns < 0)
            return new ToolExecuteResult("Error: max_turns must be 0 (unlimited) or a positive integer.", true);

        if (RunAgentDelegate == null)
            return new ToolExecuteResult("Error: agent loop not configured. Set AgentTool.RunAgentDelegate.", true);

        try
        {
            var result = await RunAgentDelegate(
                new SubagentRequest(prompt, model, effort, systemPrompt, maxTurns, context.SubagentDepth + 1, context), ct);
            return new ToolExecuteResult(ToolOutput.Limit(result, "agent"), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolExecuteResult($"Subagent error: {ex.Message}", true);
        }
    }
}
