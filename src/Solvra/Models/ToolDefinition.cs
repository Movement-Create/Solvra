using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solvra.Models;

public record ToolDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("input_schema")]
    public required ToolInputSchema InputSchema { get; init; }
}

public record ToolInputSchema
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "object";

    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement>? Properties { get; init; }

    [JsonPropertyName("required")]
    public List<string>? Required { get; init; }
}

