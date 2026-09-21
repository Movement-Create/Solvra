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
        (new Regex(@"^(o\d|chatgpt-)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "openai"),
        (new Regex(@"^gemini-", RegexOptions.IgnoreCase | RegexOptions.Compiled), "google"),
        (new Regex(@"^(llama|mistral|codellama|phi|qwen)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ollama"),
        (new Regex(@"^(moonshot-|kimi)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "moonshot"),
    ];

    private static readonly Dictionary<string, Dictionary<EffortLevel, string>> EffortModels = new()
    {
        ["anthropic"] = new()
        {
            [EffortLevel.Low] = "claude-haiku-4-5",
            [EffortLevel.Medium] = "claude-sonnet-5",
            [EffortLevel.High] = "claude-opus-5",
            [EffortLevel.Max] = "claude-opus-5"
        },
        ["openai"] = new()
        {
            [EffortLevel.Low] = "gpt-4.1-mini",
            [EffortLevel.Medium] = "gpt-4.1",
            [EffortLevel.High] = "o3",
            [EffortLevel.Max] = "o3"
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
            ["chatgpt"] = () => new ChatGptProvider(),
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

    /// <summary>
    /// Resolve a model string to a provider. Precedence:
    /// 1. an explicit "provider:model" prefix naming a registered provider;
    /// 2. <paramref name="defaultProvider"/> (the session's provider: --provider, config or env);
    /// 3. a guess from the model name; 4. anthropic.
    /// Name guessing must not override an explicit provider: gateway models such as kimi-* or
    /// qwen* were routed to Moonshot / Ollama even with --provider openai.
    /// A colon that does not follow a registered provider id is part of the model name, so
    /// Ollama tags like "llama3.1:70b" work.
    /// </summary>
    public (IProvider Provider, string Model) Resolve(string modelString, string? defaultProvider = null)
    {
        var (prefix, model) = SplitProviderPrefix(modelString);
        var providerId = prefix
            ?? (string.IsNullOrWhiteSpace(defaultProvider) ? null : defaultProvider)
            ?? DetectProvider(model)
            ?? "anthropic";
        return (GetProvider(providerId), model);
    }

    /// <summary>Split "provider:model" when the prefix is a registered provider id.</summary>
    public (string? Provider, string Model) SplitProviderPrefix(string modelString)
    {
        var colonIdx = modelString.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = modelString[..colonIdx];
            if (_providerFactories.ContainsKey(prefix))
                return (prefix, modelString[(colonIdx + 1)..]);
        }
        return (null, modelString);
    }

    /// <summary>Known provider ids used for "provider:model" parsing outside a router instance.</summary>
    public static readonly IReadOnlySet<string> BuiltinProviderIds =
        new HashSet<string>(["anthropic", "openai", "google", "ollama", "moonshot", "chatgpt"]);

    /// <summary>
    /// Pick the provider for a session. An explicit --provider or "provider:model" wins; then a
    /// provider set in config/env; only then a guess from the model name.
    /// </summary>
    public static string ChooseProvider(string model, string? explicitProvider, string? configuredProvider, bool configuredIsExplicit)
    {
        var colon = model.IndexOf(':');
        if (colon > 0 && BuiltinProviderIds.Contains(model[..colon])) return model[..colon];
        if (!string.IsNullOrWhiteSpace(explicitProvider)) return explicitProvider;
        if (configuredIsExplicit && !string.IsNullOrWhiteSpace(configuredProvider)) return configuredProvider;
        return DetectProvider(model) ?? configuredProvider ?? "anthropic";
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

        return "claude-sonnet-5"; // fallback
    }

    public IReadOnlyList<string> GetRegisteredProviderIds() =>
        _providerFactories.Keys.ToList();
}
