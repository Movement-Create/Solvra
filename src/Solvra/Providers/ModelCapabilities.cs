namespace Solvra.Providers;

public sealed record ModelDescriptor(string Id, string Label, bool Vision);

/// <summary>Conservative model capability catalogue used before a request reaches a provider.</summary>
public static class ModelCapabilities
{
    public static ModelDescriptor Describe(string provider, string model)
        => new(model, model, SupportsVision(provider, model));

    public static bool SupportsVision(string provider, string model)
    {
        var id = model.ToLowerInvariant();
        return provider.ToLowerInvariant() switch
        {
            "anthropic" => id.StartsWith("claude-3") || id.StartsWith("claude-sonnet-4") ||
                           id.StartsWith("claude-opus-4") || id.StartsWith("claude-haiku-4") ||
                           id.StartsWith("claude-sonnet-5") || id.StartsWith("claude-opus-5"),
            "google" => id.StartsWith("gemini-"),
            "chatgpt" => id.StartsWith("gpt-"),
            "openai" => id.StartsWith("gpt-4o") || id.StartsWith("gpt-4.1") || id.StartsWith("gpt-5") ||
                        id.StartsWith("gpt-6") || id.StartsWith("o3") || id.StartsWith("o4"),
            "ollama" => id.Contains("llava") || id.Contains("bakllava") || id.Contains("vision") || id.Contains("minicpm-v"),
            _ => false
        };
    }
}
