using Solvra.Models;

namespace Solvra.Core;

public enum StopReason
{
    Text,
    MaxTurns,
    MaxBudget,
    Error,
    Deadline
}

public record AgentRunOptions
{
    public required string Prompt { get; init; }
    /// <summary>Optional rich user message. When set, it replaces the text-only Prompt message.</summary>
    public Message? UserMessage { get; init; }
    public required SessionConfig Session { get; init; }
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<Message>? History { get; init; }
    public bool Streaming { get; init; }
    public Action<string>? OnText { get; init; }
    public Func<ToolCall, Task<bool>>? OnPermissionRequest { get; init; }
    public Action<ToolCall>? OnToolCall { get; init; }
    public Action<ToolResult>? OnToolResult { get; init; }
    public int SubagentDepth { get; init; }

    /// <summary>Maximum elapsed time for this run. Null means no deadline.</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>Shared monotonic deadline inherited by subagents.</summary>
    internal RunDeadline? Deadline { get; init; }

    /// <summary>Shared ownership of detached processes created by this run and its subagents.</summary>
    internal RunProcessTracker? ProcessTracker { get; init; }

    /// <summary>Working directory for tools and project instructions (default: process cwd).</summary>
    public string? Cwd { get; init; }

    /// <summary>Write the prompt, assistant turns and tool results to the session file.</summary>
    public bool LogToSession { get; init; } = true;

    /// <summary>Generation-only mode: omit tool definitions, hooks, skills, memory and project instructions.</summary>
    public bool NoTools { get; init; }
}

public record AgentRunResult
{
    public required string Text { get; init; }
    public required int Turns { get; init; }
    public required TokenUsage Usage { get; init; }
    public required decimal CostUsd { get; init; }
    public required StopReason StopReason { get; init; }
    public required IReadOnlyList<Message> Messages { get; init; }

    /// <summary>Provider/harness error that ended the run (StopReason.Error).</summary>
    public string? Error { get; init; }
}

public record SessionConfig
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public required string CreatedAt { get; init; }
    public string Model { get; init; } = "claude-sonnet-5";
    public string Provider { get; init; } = "anthropic";
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];
    public string PermissionMode { get; init; } = "default";
    public EffortLevel Effort { get; init; } = EffortLevel.Medium;
    public int MaxTurns { get; init; } = 50;
    public decimal MaxBudgetUsd { get; init; } = 5.0m;
    public int MaxTokens { get; init; } = 8192;
    public string FilePath { get; init; } = "";
}
