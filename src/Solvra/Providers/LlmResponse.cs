using Solvra.Models;

namespace Solvra.Providers;

public record LlmResponse
{
    public string? Text { get; init; }
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public required string StopReason { get; init; }
    public required TokenUsage Usage { get; init; }
}

public record CompletionOptions
{
    public required IReadOnlyList<Message> Messages { get; init; }
    public string? System { get; init; }
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public int MaxTokens { get; init; } = 8192;
    public double? Temperature { get; init; }
    public bool Stream { get; init; }
    public required string Model { get; init; }
}
