using System.Text.Json;
using System.Text.Json.Serialization;
using Solvra.Models;

namespace Solvra.Core;

public record SessionEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("data")]
    public required JsonElement Data { get; init; }
}

public record SessionInfo
{
    public required SessionConfig Config { get; init; }
    public required IReadOnlyList<Message> Messages { get; init; }
}

public sealed class SessionManager
{
    private readonly string _sessionsDir;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SessionManager(string? sessionsDir = null)
    {
        _sessionsDir = sessionsDir ?? "sessions";
    }

    public async Task<SessionConfig> CreateAsync(SessionConfig config, Dictionary<string, object?>? overrides = null)
    {
        Directory.CreateDirectory(_sessionsDir);

        var sessionId = config.Id ?? Guid.NewGuid().ToString();
        var filePath = Path.Combine(_sessionsDir, $"{sessionId}.jsonl");

        var finalConfig = config with
        {
            Id = sessionId,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            FilePath = filePath
        };

        var startData = JsonSerializer.SerializeToElement(new { config = finalConfig }, JsonOptions);
        var startEvent = new SessionEvent
        {
            Type = "session_start",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = startData
        };

        await AppendEventAsync(finalConfig, startEvent);
        return finalConfig;
    }

    public async Task AppendEventAsync(SessionConfig session, SessionEvent evt)
    {
        var line = JsonSerializer.Serialize(evt, JsonOptions) + "\n";
        await File.AppendAllTextAsync(session.FilePath, line);
    }

    public async Task LogUserMessageAsync(SessionConfig session, string content)
    {
        var data = JsonSerializer.SerializeToElement(new { content }, JsonOptions);
        await AppendEventAsync(session, new SessionEvent
        {
            Type = "user_message",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = data
        });
    }

    public async Task LogAssistantMessageAsync(SessionConfig session, string content)
    {
        var data = JsonSerializer.SerializeToElement(new { content }, JsonOptions);
        await AppendEventAsync(session, new SessionEvent
        {
            Type = "assistant_message",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = data
        });
    }

    public async Task LogResultAsync(SessionConfig session, AgentRunResult result)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            text = result.Text,
            turns = result.Turns,
            usage = new { input = result.Usage.InputTokens, output = result.Usage.OutputTokens },
            cost_usd = result.CostUsd,
            stop_reason = result.StopReason.ToString().ToLowerInvariant()
        }, JsonOptions);

        await AppendEventAsync(session, new SessionEvent
        {
            Type = "session_end",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = data
        });
    }

    public async Task<SessionInfo> ResumeAsync(string sessionId)
    {
        var filePath = Path.Combine(_sessionsDir, $"{sessionId}.jsonl");
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Session not found: {sessionId}");

        var lines = await File.ReadAllLinesAsync(filePath);
        SessionConfig? config = null;
        var messages = new List<Message>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var evt = JsonSerializer.Deserialize<SessionEvent>(line, JsonOptions);
            if (evt == null) continue;

            switch (evt.Type)
            {
                case "session_start":
                    if (evt.Data.TryGetProperty("config", out var configEl))
                        config = JsonSerializer.Deserialize<SessionConfig>(configEl.GetRawText(), JsonOptions);
                    break;

                case "user_message":
                    if (evt.Data.TryGetProperty("content", out var userContent))
                    {
                        messages.Add(Message.FromText(MessageRole.User,
                            userContent.GetString() ?? ""));
                    }
                    break;

                case "assistant_message":
                    if (evt.Data.TryGetProperty("content", out var assistantContent))
                    {
                        messages.Add(Message.FromText(MessageRole.Assistant,
                            assistantContent.GetString() ?? ""));
                    }
                    break;
            }
        }

        config ??= new SessionConfig
        {
            Id = sessionId,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            FilePath = filePath
        };

        return new SessionInfo { Config = config, Messages = messages };
    }

    public async Task<IReadOnlyList<SessionConfig>> ListAsync()
    {
        if (!Directory.Exists(_sessionsDir))
            return [];

        var configs = new List<SessionConfig>();
        foreach (var file in Directory.GetFiles(_sessionsDir, "*.jsonl"))
        {
            try
            {
                using var reader = new StreamReader(file);
                var firstLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(firstLine)) continue;

                var evt = JsonSerializer.Deserialize<SessionEvent>(firstLine, JsonOptions);
                if (evt?.Type == "session_start" && evt.Data.TryGetProperty("config", out var configEl))
                {
                    var config = JsonSerializer.Deserialize<SessionConfig>(configEl.GetRawText(), JsonOptions);
                    if (config != null)
                        configs.Add(config);
                }
            }
            catch
            {
                // Skip unreadable sessions
            }
        }

        return configs.OrderByDescending(c => c.CreatedAt).ToList();
    }

    public Task DeleteAsync(string sessionId)
    {
        var filePath = Path.Combine(_sessionsDir, $"{sessionId}.jsonl");
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }
}
