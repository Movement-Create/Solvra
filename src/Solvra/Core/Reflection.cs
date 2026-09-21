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

    private readonly bool _enabled;

    /// <param name="enabled">
    /// Off by default (config "reflection": true / SOLVRA_REFLECTION=1 turns it on): the pass
    /// re-runs the agent loop after the task, which costs a model call and used to be able to
    /// fail a run whose real work had already succeeded.
    /// </param>
    public Reflection(AgentLoop agentLoop, bool enabled = false)
    {
        _agentLoop = agentLoop;
        _enabled = enabled;
    }

    public async Task<AgentRunResult> RunAgentWithReflectionAsync(
        AgentRunOptions options,
        CancellationToken ct = default)
    {
        var result = await _agentLoop.RunAsync(options, ct);

        if (!_enabled || !ShouldReflect(result))
            return result;

        AgentRunResult reflectionResult;
        try
        {
            // Silent, unlogged, and limited to a few turns; only memory tools matter here.
            reflectionResult = await _agentLoop.RunAsync(new AgentRunOptions
            {
                Prompt = ReflectionPrompt,
                Session = options.Session with { MaxTurns = 3, AllowedTools = ["memory_note", "memory_recall"] },
                SystemPrompt = options.SystemPrompt,
                History = result.Messages,
                Streaming = false,
                OnText = null,
                OnPermissionRequest = options.OnPermissionRequest,
                OnToolCall = null,
                OnToolResult = null,
                SubagentDepth = options.SubagentDepth,
                Cwd = options.Cwd,
                LogToSession = false
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[reflection] skipped: {ex.Message}");
            return result;
        }

        // The reflection exchange is bookkeeping: keep it out of the conversation history.
        return result with
        {
            Turns = result.Turns + reflectionResult.Turns,
            Usage = result.Usage + reflectionResult.Usage,
            CostUsd = result.CostUsd + reflectionResult.CostUsd,
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
