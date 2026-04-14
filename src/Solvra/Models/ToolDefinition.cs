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

// Stub interfaces for tool system (implementation done by parallel agent)

public interface IToolRegistry
{
    void RegisterTool(ToolDefinition definition, Func<Dictionary<string, JsonElement>, CancellationToken, Task<ToolExecuteResult>> handler);
    IReadOnlyList<ToolDefinition> GetToolDefinitions();
    Task<ToolExecuteResult> ExecuteToolAsync(string name, Dictionary<string, JsonElement> input, CancellationToken ct = default);
    bool HasTool(string name);
}

public record ToolExecuteResult
{
    public required string Output { get; init; }
    public bool IsError { get; init; }
    public Dictionary<string, JsonElement>? Meta { get; init; }
}

public class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, (ToolDefinition Definition, Func<Dictionary<string, JsonElement>, CancellationToken, Task<ToolExecuteResult>> Handler)> _tools = new();

    public void RegisterTool(ToolDefinition definition, Func<Dictionary<string, JsonElement>, CancellationToken, Task<ToolExecuteResult>> handler)
    {
        _tools[definition.Name] = (definition, handler);
    }

    public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
        _tools.Values.Select(t => t.Definition).ToList();

    public async Task<ToolExecuteResult> ExecuteToolAsync(string name, Dictionary<string, JsonElement> input, CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(name, out var entry))
        {
            return new ToolExecuteResult
            {
                Output = $"Unknown tool: {name}",
                IsError = true
            };
        }

        return await entry.Handler(input, ct);
    }

    public bool HasTool(string name) => _tools.ContainsKey(name);
}
