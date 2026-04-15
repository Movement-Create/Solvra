using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solvra.Models;

public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool
}

// --- Content discriminated union ---

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ToolUseContent), "tool_use")]
[JsonDerivedType(typeof(ToolResultContent), "tool_result")]
[JsonDerivedType(typeof(ImageContent), "image")]
public abstract record MessageContent
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public record TextContent : MessageContent
{
    [JsonPropertyName("type")]
    public override string Type => "text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public record ToolUseContent : MessageContent
{
    [JsonPropertyName("type")]
    public override string Type => "tool_use";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("input")]
    public required Dictionary<string, JsonElement> Input { get; init; }
}

public record ToolResultContent : MessageContent
{
    [JsonPropertyName("type")]
    public override string Type => "tool_result";

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }
}

public record ImageSource
{
    [JsonPropertyName("type")]
    public required string SourceType { get; init; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public record ImageContent : MessageContent
{
    [JsonPropertyName("type")]
    public override string Type => "image";

    [JsonPropertyName("source")]
    public required ImageSource Source { get; init; }
}

// --- Core message ---

public record Message
{
    public required MessageRole Role { get; init; }
    public required IReadOnlyList<MessageContent> Content { get; init; }
    public string? Timestamp { get; init; }

    public static Message FromText(MessageRole role, string text) => new()
    {
        Role = role,
        Content = [new TextContent { Text = text }],
        Timestamp = DateTime.UtcNow.ToString("o")
    };

    public string GetTextContent()
    {
        return string.Join("", Content
            .OfType<TextContent>()
            .Select(c => c.Text));
    }
}

// --- Provider-agnostic tool types ---

public record ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Dictionary<string, JsonElement> Input { get; set; }
}

public record ToolResult
{
    public required string ToolUseId { get; init; }
    public required string Content { get; init; }
    public bool IsError { get; init; }
}

// --- Usage ---

public record TokenUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new()
    {
        InputTokens = a.InputTokens + b.InputTokens,
        OutputTokens = a.OutputTokens + b.OutputTokens
    };
}

// --- Message role helpers ---

public static class MessageRoleExtensions
{
    public static string ToWireString(this MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        _ => "user"
    };

    public static MessageRole ParseRole(string value) => value.ToLowerInvariant() switch
    {
        "system" => MessageRole.System,
        "user" => MessageRole.User,
        "assistant" => MessageRole.Assistant,
        "tool" => MessageRole.Tool,
        _ => MessageRole.User
    };
}
