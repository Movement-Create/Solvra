using Solvra.Models;

namespace Solvra.Core;

public sealed class Reflection
{
    private readonly AgentLoop _agentLoop;
    private const int LongTaskTurnThreshold = 5;

    private const string ReflectionPrompt =
        "Before finishing: is there anything future-you should remember from this task? " +
        "If you made a mistake, hit an unexpected error, or learned a non-obvious gotcha, " +
        "call memory_note with kind='lesson' and short relevant tags (file paths, tool names, topics). " +
        "If nothing is worth saving, reply with just 'done'.";

    public Reflection(AgentLoop agentLoop)
    {
        _agentLoop = agentLoop;
    }

    public async Task<AgentRunResult> RunAgentWithReflectionAsync(
        AgentRunOptions options,
        CancellationToken ct = default)
    {
        var result = await _agentLoop.RunAsync(options, ct);

        if (!ShouldReflect(result))
            return result;

        // Run reflection turn (silenced — no onText callback)
        var reflectionResult = await _agentLoop.RunAsync(new AgentRunOptions
        {
            Prompt = ReflectionPrompt,
            Session = options.Session,
            SystemPrompt = options.SystemPrompt,
            History = result.Messages,
            Streaming = false,
            OnText = null,
            OnPermissionRequest = options.OnPermissionRequest,
            OnToolCall = null,
            OnToolResult = null,
            SubagentDepth = options.SubagentDepth
        }, ct);

        // Combine results: primary text, combined usage/cost/turns
        return new AgentRunResult
        {
            Text = result.Text,
            Turns = result.Turns + reflectionResult.Turns,
            Usage = result.Usage + reflectionResult.Usage,
            CostUsd = result.CostUsd + reflectionResult.CostUsd,
            StopReason = result.StopReason,
            Messages = reflectionResult.Messages
        };
    }

    private static bool ShouldReflect(AgentRunResult result)
    {
        if (result.StopReason != StopReason.Text)
            return false;

        if (result.Turns >= LongTaskTurnThreshold)
            return true;

        return HasToolError(result.Messages);
    }

    private static bool HasToolError(IReadOnlyList<Message> messages)
    {
        foreach (var msg in messages)
        {
            if (msg.Role != MessageRole.Tool) continue;
            foreach (var block in msg.Content)
            {
                if (block is ToolResultContent { IsError: true })
                    return true;
            }
        }
        return false;
    }
}
