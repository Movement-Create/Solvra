using System.Text.RegularExpressions;
using Solvra.Models;

namespace Solvra.Providers;

public sealed class ModelRouter
{
    private readonly Dictionary<string, Func<IProvider>> _providerFactories;
    private readonly Dictionary<string, IProvider> _providerCache = new();

    private static readonly (Regex Pattern, string ProviderId)[] ModelPrefixMap =
    [
        (new Regex(@"^claude-", RegexOptions.IgnoreCase | RegexOptions.Compiled), "anthropic"),
        (new Regex(@"^gpt-", RegexOptions.IgnoreCase | RegexOptions.Compiled), "openai"),
        (new Regex(@"^o1", RegexOptions.IgnoreCase | RegexOptions.Compiled), "openai"),
        (new Regex(@"^gemini-", RegexOptions.IgnoreCase | RegexOptions.Compiled), "google"),
        (new Regex(@"^(llama|mistral|codellama|phi|qwen)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ollama"),
        (new Regex(@"^moonshot-", RegexOptions.IgnoreCase | RegexOptions.Compiled), "moonshot"),
    ];

    private static readonly Dictionary<string, Dictionary<EffortLevel, string>> EffortModels = new()
    {
        ["anthropic"] = new()
        {
            [EffortLevel.Low] = "claude-3-5-haiku-20241022",
            [EffortLevel.Medium] = "claude-3-5-sonnet-20241022",
            [EffortLevel.High] = "claude-3-5-sonnet-20241022",
            [EffortLevel.Max] = "claude-3-opus-20240229"
        },
        ["openai"] = new()
        {
            [EffortLevel.Low] = "gpt-4o-mini",
            [EffortLevel.Medium] = "gpt-4o",
            [EffortLevel.High] = "gpt-4o",
            [EffortLevel.Max] = "o1-preview"
        },
        ["google"] = new()
        {
            [EffortLevel.Low] = "gemini-2.0-flash-lite",
            [EffortLevel.Medium] = "gemini-2.5-flash",
            [EffortLevel.High] = "gemini-2.5-flash",
            [EffortLevel.Max] = "gemini-2.5-pro"
        },
        ["ollama"] = new()
        {
            [EffortLevel.Low] = "llama3.2",
            [EffortLevel.Medium] = "llama3.1",
            [EffortLevel.High] = "llama3.1:70b",
            [EffortLevel.Max] = "llama3.1:405b"
        },
    };

    private static readonly string[] AutoSelectOrder = ["anthropic", "openai", "google", "ollama"];

    public ModelRouter(Dictionary<string, Func<IProvider>>? providerFactories = null)
    {
        _providerFactories = providerFactories ?? new Dictionary<string, Func<IProvider>>
        {
            ["anthropic"] = () => new AnthropicProvider(),
            ["openai"] = () => new OpenAiProvider(),
            ["google"] = () => new GoogleProvider(),
            ["ollama"] = () => new OllamaProvider(),
            ["moonshot"] = () => new MoonshotProvider(),
        };
    }

    public IProvider GetProvider(string providerId)
    {
        if (_providerCache.TryGetValue(providerId, out var cached))
            return cached;

        if (!_providerFactories.TryGetValue(providerId, out var factory))
            throw new ArgumentException($"Unknown provider: {providerId}");

        var provider = factory();
        _providerCache[providerId] = provider;
        return provider;
    }

    public (IProvider Provider, string Model) Resolve(string modelString, string? defaultProvider = null)
    {
        string providerId;
        string model;

        // Check for explicit provider:model syntax
        var colonIdx = modelString.IndexOf(':');
        if (colonIdx > 0)
        {
            providerId = modelString[..colonIdx];
            model = modelString[(colonIdx + 1)..];
        }
        else
        {
            model = modelString;
            providerId = DetectProvider(model) ?? defaultProvider ?? "anthropic";
        }

        return (GetProvider(providerId), model);
    }

    public static string? DetectProvider(string model)
    {
        foreach (var (pattern, providerId) in ModelPrefixMap)
        {
            if (pattern.IsMatch(model))
                return providerId;
        }
        return null;
    }

    public async Task<(IProvider Provider, string Model)?> AutoSelectAsync(EffortLevel effort, CancellationToken ct = default)
    {
        foreach (var providerId in AutoSelectOrder)
        {
            if (!_providerFactories.ContainsKey(providerId)) continue;

            try
            {
                var provider = GetProvider(providerId);
                if (await provider.ValidateAsync(ct))
                {
                    var model = GetEffortModel(providerId, effort);
                    return (provider, model);
                }
            }
            catch
            {
                // Provider unavailable, try next
            }
        }
        return null;
    }

    public static string GetEffortModel(string providerId, EffortLevel effort)
    {
        if (EffortModels.TryGetValue(providerId, out var models) &&
            models.TryGetValue(effort, out var model))
            return model;

        return "claude-3-5-sonnet-20241022"; // fallback
    }

    public IReadOnlyList<string> GetRegisteredProviderIds() =>
        _providerFactories.Keys.ToList();
}
