#nullable enable

using Solvra.Models;

namespace Solvra.Core;

/// <summary>Live callbacks for one agent (the main run or one subagent).</summary>
public sealed record AgentHooks(
    Action<string>? OnText,
    Action<string>? OnThinking,
    Action<ToolCall>? OnToolCall,
    Action<ToolResult>? OnToolResult,
    Func<ToolCall, Task<bool>>? OnPermissionRequest,
    Action<TokenUsage, decimal, int>? OnUsage);

/// <summary>
/// Observes a run and every subagent it starts, so a UI can show nested agents live
/// (see <see cref="StreamJsonRun"/>). Null for the plain CLI paths, which keep their behaviour.
/// </summary>
public interface IAgentObserver
{
    /// <summary>Hooks for <paramref name="agentId"/>.</summary>
    AgentHooks HooksFor(string agentId);

    /// <summary>A subagent started from tool call <paramref name="toolCallId"/> of <paramref name="parentAgentId"/>; returns its id.</summary>
    string SubagentStarted(string parentAgentId, string? toolCallId, string title);

    /// <summary>The subagent finished: status is ok, error or cancelled.</summary>
    void SubagentEnded(string agentId, string status);

    /// <summary>Text the user gave with a denial, consumed once and appended to the refusal the model sees.</summary>
    string? TakeDenialNote(string toolCallId);

    /// <summary>Whether several agent tool calls from one turn may run at the same time.</summary>
    bool ParallelSubagents { get; }
}
