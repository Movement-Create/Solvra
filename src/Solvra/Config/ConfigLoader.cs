using System.Text.Json;
using System.Text.RegularExpressions;

namespace Solvra.Config;

public static partial class ConfigLoader
{
    private static readonly string[] LocalConfigNames =
    [
        "solvra.json5",
        "solvra.json",
        ".solvra.json5",
        ".solvra.json"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task<SolvraConfig> LoadAsync(string? configPath = null)
    {
        SolvraConfig config = new();

        // 1. Global config: ~/.solvra/config.json5
        var globalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".solvra", "config.json5");
        config = await MergeFromFileAsync(config, globalPath);

        // 2. Local config (first found in cwd)
        if (configPath != null)
        {
            config = await MergeFromFileAsync(config, configPath);
        }
        else
        {
            foreach (var name in LocalConfigNames)
            {
                if (File.Exists(name))
                {
                    config = await MergeFromFileAsync(config, name);
                    break;
                }
            }
        }

        // 3. Environment variable overrides
        config = ApplyEnvironmentOverrides(config);

        return config;
    }

    /// <summary>
    /// Fix 6g: Replace default-value sentinel checks with JSON property existence checks.
    /// Only override fields that are explicitly present in the JSON.
    /// </summary>
    private static async Task<SolvraConfig> MergeFromFileAsync(SolvraConfig current, string path)
    {
        if (!File.Exists(path)) return current;

        try
        {
            var raw = await File.ReadAllTextAsync(path);
            var cleanJson = StripJson5(raw);

            using var doc = JsonDocument.Parse(cleanJson);
            var json = doc.RootElement;

            // Only override fields that are explicitly set in the JSON
            var result = current;

            if (json.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
                result = result with { Model = modelProp.GetString()! };

            if (json.TryGetProperty("provider", out var providerProp) && providerProp.ValueKind == JsonValueKind.String)
                result = result with { Provider = providerProp.GetString()!, ProviderIsExplicit = true };

            if (json.TryGetProperty("effort", out var effortProp) && effortProp.ValueKind == JsonValueKind.String)
                result = result with { Effort = effortProp.GetString()! };

            if (json.TryGetProperty("max_turns", out var maxTurnsProp) && maxTurnsProp.ValueKind == JsonValueKind.Number)
                result = result with { MaxTurns = maxTurnsProp.GetInt32() };

            if (json.TryGetProperty("max_budget_usd", out var maxBudgetProp) && maxBudgetProp.ValueKind == JsonValueKind.Number)
                result = result with { MaxBudgetUsd = maxBudgetProp.GetDecimal() };

            if (json.TryGetProperty("permission_mode", out var permProp) && permProp.ValueKind == JsonValueKind.String)
                result = result with { PermissionMode = permProp.GetString()! };

            if (json.TryGetProperty("system_prompt", out var sysProp) && sysProp.ValueKind == JsonValueKind.String)
                result = result with { SystemPrompt = sysProp.GetString() };

            if (json.TryGetProperty("skills_dir", out var skillsProp) && skillsProp.ValueKind == JsonValueKind.String)
                result = result with { SkillsDir = skillsProp.GetString()! };

            if (json.TryGetProperty("memory_dir", out var memoryProp) && memoryProp.ValueKind == JsonValueKind.String)
                result = result with { MemoryDir = memoryProp.GetString()! };

            if (json.TryGetProperty("sessions_dir", out var sessionsProp) && sessionsProp.ValueKind == JsonValueKind.String)
                result = result with { SessionsDir = sessionsProp.GetString()! };

            if (json.TryGetProperty("allowed_tools", out var allowedProp) && allowedProp.ValueKind == JsonValueKind.Array)
            {
                var tools = allowedProp.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
                result = result with { AllowedTools = tools };
            }

            if (json.TryGetProperty("disallowed_tools", out var disallowedProp) && disallowedProp.ValueKind == JsonValueKind.Array)
            {
                var tools = disallowedProp.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
                result = result with { DisallowedTools = tools };
            }

            if (json.TryGetProperty("reflection", out var reflProp) && reflProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                result = result with { Reflection = reflProp.GetBoolean() };

            if (json.TryGetProperty("max_tokens", out var maxTokProp) && maxTokProp.ValueKind == JsonValueKind.Number)
                result = result with { MaxTokens = maxTokProp.GetInt32() };

            if (json.TryGetProperty("hooks", out var hooksProp) && hooksProp.ValueKind == JsonValueKind.Object)
            {
                static List<string> Commands(JsonElement obj, string name) =>
                    obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
                        ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                        : [];
                result = result with
                {
                    Hooks = new HooksConfig
                    {
                        PreToolUse = Commands(hooksProp, "PreToolUse"),
                        PostToolUse = Commands(hooksProp, "PostToolUse"),
                        Stop = Commands(hooksProp, "Stop"),
                    }
                };
            }

            if (json.TryGetProperty("webhook_secret", out var webhookProp) && webhookProp.ValueKind == JsonValueKind.String)
                result = result with { WebhookSecret = webhookProp.GetString() };

            if (json.TryGetProperty("cron", out var cronProp) && cronProp.ValueKind == JsonValueKind.Array)
            {
                var cronJobs = JsonSerializer.Deserialize<List<CronJobConfig>>(cronProp.GetRawText(), JsonOptions)
                    ?? new List<CronJobConfig>();
                result = result with { Cron = cronJobs };
            }

            // Handle nested observability config
            if (json.TryGetProperty("observability", out var obsProp) && obsProp.ValueKind == JsonValueKind.Object)
            {
                var obs = result.Observability;
                if (obsProp.TryGetProperty("enabled", out var enabledProp))
                    obs = obs with { Enabled = enabledProp.GetBoolean() };
                if (obsProp.TryGetProperty("level", out var levelProp) && levelProp.ValueKind == JsonValueKind.String)
                    obs = obs with { Level = levelProp.GetString()! };
                if (obsProp.TryGetProperty("narrate", out var narrateProp))
                    obs = obs with { Narrate = narrateProp.GetBoolean() };
                if (obsProp.TryGetProperty("otel_endpoint", out var otelProp) && otelProp.ValueKind == JsonValueKind.String)
                    obs = obs with { OtelEndpoint = otelProp.GetString() };
                result = result with { Observability = obs };
            }

            return result;
        }
        catch (Exception ex)
        {
            // Never drop a broken config silently: the user would run with defaults unknowingly.
            Console.Error.WriteLine($"[config] Ignoring {path}: {ex.Message}");
            return current;
        }
    }

    private static SolvraConfig ApplyEnvironmentOverrides(SolvraConfig config)
    {
        var model = Environment.GetEnvironmentVariable("SOLVRA_MODEL");
        var provider = Environment.GetEnvironmentVariable("SOLVRA_PROVIDER");
        var effort = Environment.GetEnvironmentVariable("SOLVRA_EFFORT");
        var maxTurns = Environment.GetEnvironmentVariable("SOLVRA_MAX_TURNS");
        var maxBudget = Environment.GetEnvironmentVariable("SOLVRA_MAX_BUDGET");
        var permMode = Environment.GetEnvironmentVariable("SOLVRA_PERMISSION_MODE");
        var sysPrompt = Environment.GetEnvironmentVariable("SOLVRA_SYSTEM_PROMPT");
        var obsLevel = Environment.GetEnvironmentVariable("SOLVRA_OBS_LEVEL");
        var obsNarrate = Environment.GetEnvironmentVariable("SOLVRA_OBS_NARRATE");
        var otelEndpoint = Environment.GetEnvironmentVariable("SOLVRA_OTEL_ENDPOINT");
        var sessionsDir = Environment.GetEnvironmentVariable("SOLVRA_SESSIONS_DIR");
        var memoryDir = Environment.GetEnvironmentVariable("SOLVRA_MEMORY_DIR");
        var reflection = Environment.GetEnvironmentVariable("SOLVRA_REFLECTION");
        var maxTokens = Environment.GetEnvironmentVariable("SOLVRA_MAX_TOKENS");

        return config with
        {
            Model = !string.IsNullOrEmpty(model) ? model : config.Model,
            Provider = !string.IsNullOrEmpty(provider) ? provider : config.Provider,
            ProviderIsExplicit = config.ProviderIsExplicit || !string.IsNullOrEmpty(provider),
            Effort = !string.IsNullOrEmpty(effort) ? effort : config.Effort,
            MaxTurns = int.TryParse(maxTurns, out var mt) ? mt : config.MaxTurns,
            MaxBudgetUsd = decimal.TryParse(maxBudget, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var mb) ? mb : config.MaxBudgetUsd,
            Reflection = reflection is "1" or "true" ? true : reflection is "0" or "false" ? false : config.Reflection,
            MaxTokens = int.TryParse(maxTokens, out var mtk) && mtk > 0 ? mtk : config.MaxTokens,
            PermissionMode = !string.IsNullOrEmpty(permMode) ? permMode : config.PermissionMode,
            SystemPrompt = !string.IsNullOrEmpty(sysPrompt) ? sysPrompt : config.SystemPrompt,
            SessionsDir = !string.IsNullOrEmpty(sessionsDir) ? sessionsDir : config.SessionsDir,
            MemoryDir = !string.IsNullOrEmpty(memoryDir) ? memoryDir : config.MemoryDir,
            Observability = config.Observability with
            {
                Level = !string.IsNullOrEmpty(obsLevel) ? obsLevel : config.Observability.Level,
                Narrate = obsNarrate is "1" or "true" || config.Observability.Narrate,
                OtelEndpoint = !string.IsNullOrEmpty(otelEndpoint) ? otelEndpoint : config.Observability.OtelEndpoint
            }
        };
    }

    /// <summary>
    /// Remove // and /* */ comments and trailing commas, leaving string contents alone
    /// (the old regex version cut "https://..." URLs in half and broke the whole file).
    /// </summary>
    internal static string StripJson5(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length);
        var inString = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < input.Length) { sb.Append(input[++i]); continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; sb.Append(c); continue; }
            if (c == '/' && i + 1 < input.Length && input[i + 1] == '/')
            {
                while (i < input.Length && input[i] != '\n') i++;
                if (i < input.Length) sb.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < input.Length && input[i + 1] == '*')
            {
                var end = input.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? input.Length : end + 1;
                continue;
            }
            sb.Append(c);
        }
        return TrailingCommaRegex().Replace(sb.ToString(), "$1");
    }

    [GeneratedRegex(@",\s*([\]}])")]
    private static partial Regex TrailingCommaRegex();
}
