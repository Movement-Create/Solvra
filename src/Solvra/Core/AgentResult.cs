using Solvra.Models;

namespace Solvra.Core;

public enum StopReason
{
    Text,
    MaxTurns,
    MaxBudget,
    Error
}

public record AgentRunOptions
{
    public required string Prompt { get; init; }
    public required SessionConfig Session { get; init; }
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<Message>? History { get; init; }
    public bool Streaming { get; init; }
    public Action<string>? OnText { get; init; }
    public Func<ToolCall, Task<bool>>? OnPermissionRequest { get; init; }
    public Action<ToolCall>? OnToolCall { get; init; }
    public Action<ToolResult>? OnToolResult { get; init; }
    public int SubagentDepth { get; init; }
}

public record AgentRunResult
{
    public required string Text { get; init; }
    public required int Turns { get; init; }
    public required TokenUsage Usage { get; init; }
    public required decimal CostUsd { get; init; }
    public required StopReason StopReason { get; init; }
    public required IReadOnlyList<Message> Messages { get; init; }
}

public record SessionConfig
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public required string CreatedAt { get; init; }
    public string Model { get; init; } = "claude-3-5-sonnet-20241022";
    public string Provider { get; init; } = "anthropic";
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];
    public string PermissionMode { get; init; } = "default";
    public EffortLevel Effort { get; init; } = EffortLevel.Medium;
    public int MaxTurns { get; init; } = 50;
    public decimal MaxBudgetUsd { get; init; } = 1.0m;
    public string FilePath { get; init; } = "";
}
