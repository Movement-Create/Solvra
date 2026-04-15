using System.Text.Json.Serialization;
using Solvra.Models;

namespace Solvra.Config;

public record SolvraConfig
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = "claude-3-5-sonnet-20241022";

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "anthropic";

    [JsonPropertyName("effort")]
    public string Effort { get; init; } = "medium";

    [JsonPropertyName("max_turns")]
    public int MaxTurns { get; init; } = 50;

    [JsonPropertyName("max_budget_usd")]
    public decimal MaxBudgetUsd { get; init; } = 1.0m;

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
    public string MemoryDir { get; init; } = "./memory";

    [JsonPropertyName("sessions_dir")]
    public string SessionsDir { get; init; } = "./sessions";

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
