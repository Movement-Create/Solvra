using Solvra.Models;

namespace Solvra.Providers;

/// <summary>
/// Moonshot AI provider — OpenAI-compatible format with different base URL.
/// </summary>
public sealed class MoonshotProvider : IProvider
{
    private readonly OpenAiProvider _inner;

    public string Id => "moonshot";
    public string DisplayName => "Moonshot AI";

    private static readonly Dictionary<string, (decimal Input, decimal Output)> Pricing = new()
    {
        ["moonshot-v1-8k"] = (1m, 2m),
        ["moonshot-v1-32k"] = (2m, 4m),
        ["moonshot-v1-128k"] = (6m, 12m),
        ["kimi-k2.5"] = (2m, 8m),
        ["kimi-k2-thinking"] = (2m, 8m),
        ["kimi-k2-turbo-preview"] = (1m, 4m),
        ["kimi-k2-thinking-turbo"] = (1m, 4m),
    };

    private static readonly (decimal Input, decimal Output) DefaultPricing = (2m, 4m);

    public MoonshotProvider(HttpClient? http = null, string? apiKey = null)
    {
        var key = apiKey ?? Environment.GetEnvironmentVariable("MOONSHOT_API_KEY") ?? "";
        _inner = new OpenAiProvider(http, key, "https://api.moonshot.ai/v1");
    }

    public Task<LlmResponse> CompleteAsync(CompletionOptions options, CancellationToken ct = default)
        => _inner.CompleteAsync(options, ct);

    public IAsyncEnumerable<StreamEvent> StreamAsync(CompletionOptions options, CancellationToken ct = default)
        => _inner.StreamAsync(options, ct);

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
        => ["kimi-k2.5", "kimi-k2-thinking", "kimi-k2-turbo-preview", "kimi-k2-thinking-turbo", "moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k"];

    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        var key = Environment.GetEnvironmentVariable("MOONSHOT_API_KEY");
        return !string.IsNullOrEmpty(key);
    }

    public decimal EstimateCost(string model, int inputTokens, int outputTokens)
    {
        var pricing = Pricing.GetValueOrDefault(model, DefaultPricing);
        return (inputTokens * pricing.Input + outputTokens * pricing.Output) / 1_000_000m;
    }
}
