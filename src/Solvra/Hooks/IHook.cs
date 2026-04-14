#nullable enable

using System.Text.Json;

namespace Solvra.Hooks;

public enum HookEvent
{
    PreToolUse,
    PostToolUse,
    Stop
}

public enum HookAction
{
    Allow,
    Block,
    Modify
}

public record HookContext(
    HookEvent Event,
    string SessionId,
    int Turn,
    ToolCallInfo? ToolCall = null,
    ToolResultInfo? ToolResult = null,
    string? FinalText = null);

public record ToolCallInfo(string Id, string Name, JsonElement Input);

public record ToolResultInfo(string Output, bool IsError);

public record HookResult(
    HookAction Action,
    JsonElement? ModifiedInput = null,
    string? ModifiedOutput = null,
    string? Reason = null);

public interface IHook
{
    string Id { get; }
    HookEvent Event { get; }
    string[]? ToolFilter { get; }
    Task<HookResult> ExecuteAsync(HookContext context);
}
