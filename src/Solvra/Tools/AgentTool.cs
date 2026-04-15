#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

/// <summary>
/// Spawn a subagent. Uses lazy loading to avoid circular dependency with the agent loop.
/// </summary>
public class AgentTool : ToolBase
{
    private const int MaxSubagentDepth = 2;

    /// <summary>
    /// Delegate that runs the agent loop. Set by the agent loop module to avoid circular dependencies.
    /// </summary>
    public static Func<string, string?, string?, int, CancellationToken, Task<string>>? RunAgentDelegate { get; set; }

    public override string Name => "agent";
    public override string Description => "Spawn a subagent to handle a sub-task.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Agent;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            prompt = new { type = "string", description = "Prompt for the subagent" },
            model = new { type = "string", description = "Model to use (optional)" },
            system_prompt = new { type = "string", description = "Custom system prompt (optional)" },
            max_turns = new { type = "integer", description = "Maximum turns (default 20)", @default = 20 }
        },
        required = new[] { "prompt" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var prompt = GetString(input, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return new ToolExecuteResult("Error: prompt is required", true);

        if (RunAgentDelegate == null)
            return new ToolExecuteResult("Error: agent loop not configured. Set AgentTool.RunAgentDelegate.", true);

        // Depth cap: check SubagentDepth from context env
        if (context.Env.TryGetValue("__subagent_depth", out var depthStr) &&
            int.TryParse(depthStr, out var currentDepth) &&
            currentDepth >= MaxSubagentDepth)
        {
            return new ToolExecuteResult($"Error: maximum subagent nesting depth ({MaxSubagentDepth}) reached.", true);
        }

        var model = GetOptionalString(input, "model");
        var systemPrompt = GetOptionalString(input, "system_prompt");
        var maxTurns = GetInt(input, "max_turns", 20);

        try
        {
            var result = await RunAgentDelegate(prompt, model, systemPrompt, maxTurns, ct);
            return new ToolExecuteResult(result, false);
        }
        catch (Exception ex)
        {
            return new ToolExecuteResult($"Subagent error: {ex.Message}", true);
        }
    }
}
