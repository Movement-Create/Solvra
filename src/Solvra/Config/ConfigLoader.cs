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

    private static async Task<SolvraConfig> MergeFromFileAsync(SolvraConfig current, string path)
    {
        if (!File.Exists(path)) return current;

        try
        {
            var raw = await File.ReadAllTextAsync(path);
            var cleanJson = StripJson5(raw);
            var loaded = JsonSerializer.Deserialize<SolvraConfig>(cleanJson, JsonOptions);
            if (loaded == null) return current;

            // Merge non-default values from loaded into current
            return current with
            {
                Model = loaded.Model != "claude-3-5-sonnet-20241022" ? loaded.Model : current.Model,
                Provider = loaded.Provider != "anthropic" ? loaded.Provider : current.Provider,
                Effort = loaded.Effort != "medium" ? loaded.Effort : current.Effort,
                MaxTurns = loaded.MaxTurns != 50 ? loaded.MaxTurns : current.MaxTurns,
                MaxBudgetUsd = loaded.MaxBudgetUsd != 1.0m ? loaded.MaxBudgetUsd : current.MaxBudgetUsd,
                PermissionMode = loaded.PermissionMode != "default" ? loaded.PermissionMode : current.PermissionMode,
                AllowedTools = loaded.AllowedTools.Count > 0 ? loaded.AllowedTools : current.AllowedTools,
                DisallowedTools = loaded.DisallowedTools.Count > 0 ? loaded.DisallowedTools : current.DisallowedTools,
                SystemPrompt = loaded.SystemPrompt ?? current.SystemPrompt,
                SkillsDir = loaded.SkillsDir != "./skills" ? loaded.SkillsDir : current.SkillsDir,
                MemoryDir = loaded.MemoryDir != "./memory" ? loaded.MemoryDir : current.MemoryDir,
                SessionsDir = loaded.SessionsDir != "./sessions" ? loaded.SessionsDir : current.SessionsDir,
            };
        }
        catch
        {
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

        return config with
        {
            Model = !string.IsNullOrEmpty(model) ? model : config.Model,
            Provider = !string.IsNullOrEmpty(provider) ? provider : config.Provider,
            Effort = !string.IsNullOrEmpty(effort) ? effort : config.Effort,
            MaxTurns = int.TryParse(maxTurns, out var mt) ? mt : config.MaxTurns,
            MaxBudgetUsd = decimal.TryParse(maxBudget, out var mb) ? mb : config.MaxBudgetUsd,
            PermissionMode = !string.IsNullOrEmpty(permMode) ? permMode : config.PermissionMode,
            SystemPrompt = !string.IsNullOrEmpty(sysPrompt) ? sysPrompt : config.SystemPrompt,
            Observability = config.Observability with
            {
                Level = !string.IsNullOrEmpty(obsLevel) ? obsLevel : config.Observability.Level,
                Narrate = obsNarrate is "1" or "true" || config.Observability.Narrate,
                OtelEndpoint = !string.IsNullOrEmpty(otelEndpoint) ? otelEndpoint : config.Observability.OtelEndpoint
            }
        };
    }

    internal static string StripJson5(string input)
    {
        // Remove single-line comments
        var result = SingleLineCommentRegex().Replace(input, "");
        // Remove multi-line comments
        result = MultiLineCommentRegex().Replace(result, "");
        // Remove trailing commas before } or ]
        result = TrailingCommaRegex().Replace(result, "$1");
        return result;
    }

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex SingleLineCommentRegex();

    [GeneratedRegex(@"/\*[\s\S]*?\*/")]
    private static partial Regex MultiLineCommentRegex();

    [GeneratedRegex(@",\s*([\]}])")]
    private static partial Regex TrailingCommaRegex();
}
