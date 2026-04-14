#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

/// <summary>
/// Base class for tools that provides common schema-building helpers.
/// </summary>
public abstract class ToolBase : ITool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract PermissionLevel PermissionLevel { get; }
    public abstract JsonElement GetInputSchema();
    public abstract Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default);

    protected static JsonElement BuildSchema(object schemaObj)
    {
        var json = JsonSerializer.Serialize(schemaObj);
        return JsonDocument.Parse(json).RootElement;
    }

    protected static string GetString(JsonElement input, string property)
    {
        return input.TryGetProperty(property, out var val) ? val.GetString() ?? "" : "";
    }

    protected static string? GetOptionalString(JsonElement input, string property)
    {
        return input.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;
    }

    protected static int GetInt(JsonElement input, string property, int defaultValue)
    {
        return input.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.Number
            ? val.GetInt32()
            : defaultValue;
    }

    protected static int? GetOptionalInt(JsonElement input, string property)
    {
        return input.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.Number
            ? val.GetInt32()
            : null;
    }

    protected static string[] GetStringArray(JsonElement input, string property)
    {
        if (!input.TryGetProperty(property, out var val) || val.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return val.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }
}
