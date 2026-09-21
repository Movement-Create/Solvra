using System.Text.Json.Serialization;
using Solvra.Models;

namespace Solvra.Config;

public record SolvraConfig
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = "claude-sonnet-5";

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "anthropic";

    /// <summary>True when the provider came from a config file or SOLVRA_PROVIDER (not the built-in default).</summary>
    [JsonIgnore]
    public bool ProviderIsExplicit { get; init; }

    [JsonPropertyName("effort")]
    public string Effort { get; init; } = "medium";

    [JsonPropertyName("max_turns")]
    public int MaxTurns { get; init; } = 50;

    [JsonPropertyName("max_budget_usd")]
    public decimal MaxBudgetUsd { get; init; } = 5.0m;

    [JsonPropertyName("permission_mode")]
    public string PermissionMode { get; init; } = "default";

    [JsonPropertyName("allowed_tools")]
    public List<string> AllowedTools { get; init; } = [];

    [JsonPropertyName("disallowed_tools")]
    public List<string> DisallowedTools { get; init; } = [];

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("skills_dir")]
    public string SkillsDir { get; init; } = "./skills";

    [JsonPropertyName("memory_dir")]
    public string MemoryDir { get; init; } = SolvraPaths.MemoryDir;

    [JsonPropertyName("sessions_dir")]
    public string SessionsDir { get; init; } = SolvraPaths.SessionsDir;

    [JsonPropertyName("hooks")]
    public HooksConfig Hooks { get; init; } = new();

    [JsonPropertyName("observability")]
    public ObservabilityConfig Observability { get; init; } = new();

    /// <summary>
    /// Fix 6d: Cron job definitions with Name, Schedule, Prompt, Model.
    /// </summary>
    [JsonPropertyName("cron")]
    public List<CronJobConfig> Cron { get; init; } = [];

    /// <summary>
    /// Webhook authentication secret.
    /// </summary>
    [JsonPropertyName("webhook_secret")]
    public string? WebhookSecret { get; init; }

    /// <summary>
    /// Run the post-task "reflection" pass that asks the model to save lessons to memory.
    /// Off by default: it re-runs the agent loop (extra cost, extra failure point).
    /// </summary>
    [JsonPropertyName("reflection")]
    public bool Reflection { get; init; }

    /// <summary>Max output tokens per model call.</summary>
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; } = 8192;

    public EffortLevel ParsedEffort => EffortLevelExtensions.Parse(Effort);
}

public record HooksConfig
{
    [JsonPropertyName("PreToolUse")]
    public List<string> PreToolUse { get; init; } = [];

    [JsonPropertyName("PostToolUse")]
    public List<string> PostToolUse { get; init; } = [];

    [JsonPropertyName("Stop")]
    public List<string> Stop { get; init; } = [];
}

public record ObservabilityConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("level")]
    public string Level { get; init; } = "full";

    [JsonPropertyName("narrate")]
    public bool Narrate { get; init; }

    [JsonPropertyName("otel_endpoint")]
    public string? OtelEndpoint { get; init; }
}

public record CronJobConfig
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("schedule")]
    public required string Schedule { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    /// <summary>
    /// Fix 6d: Optional model override for this cron job.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}
