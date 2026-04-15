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

    /// <summary>
    /// Fix 5f: Write a tool_call event to the session JSONL.
    /// </summary>
    public async Task AppendToolCallAsync(SessionConfig session, string toolName, JsonElement input, string callId)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            tool_name = toolName,
            input,
            call_id = callId
        }, JsonOptions);

        await AppendEventAsync(session, new SessionEvent
        {
            Type = "tool_call",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = data
        });
    }

    /// <summary>
    /// Fix 5f: Write a tool_result event to the session JSONL.
    /// </summary>
    public async Task AppendToolResultAsync(SessionConfig session, string callId, string output, bool isError)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            call_id = callId,
            output,
            is_error = isError
        }, JsonOptions);

        await AppendEventAsync(session, new SessionEvent
        {
            Type = "tool_result",
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

    /// <summary>
    /// Fix 5g: Resume a session, restoring tool_call/tool_result events
    /// as proper ToolUseContent/ToolResultContent message blocks.
    /// </summary>
    public async Task<SessionInfo> ResumeAsync(string sessionId)
    {
        var filePath = Path.Combine(_sessionsDir, $"{sessionId}.jsonl");
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Session not found: {sessionId}");

        var lines = await File.ReadAllLinesAsync(filePath);
        SessionConfig? config = null;
        var messages = new List<Message>();

        // Track pending tool calls/results to build proper message blocks
        var pendingToolUses = new List<MessageContent>();
        var pendingToolResults = new List<MessageContent>();

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
                    // Flush any pending tool results before user message
                    FlushToolMessages(messages, ref pendingToolUses, ref pendingToolResults);

                    if (evt.Data.TryGetProperty("content", out var userContent))
                    {
                        messages.Add(Message.FromText(MessageRole.User,
                            userContent.GetString() ?? ""));
                    }
                    break;

                case "assistant_message":
                    // Flush any pending tool results before assistant message
                    FlushToolMessages(messages, ref pendingToolUses, ref pendingToolResults);

                    if (evt.Data.TryGetProperty("content", out var assistantContent))
                    {
                        messages.Add(Message.FromText(MessageRole.Assistant,
                            assistantContent.GetString() ?? ""));
                    }
                    break;

                case "tool_call":
                    // If we have pending tool results, flush them first
                    if (pendingToolResults.Count > 0)
                        FlushToolMessages(messages, ref pendingToolUses, ref pendingToolResults);

                    var toolName = evt.Data.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
                    var callId = evt.Data.TryGetProperty("call_id", out var ci) ? ci.GetString() ?? "" : "";
                    var inputEl = evt.Data.TryGetProperty("input", out var inp) ? inp : default;

                    var toolInput = new Dictionary<string, JsonElement>();
                    if (inputEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in inputEl.EnumerateObject())
                            toolInput[prop.Name] = prop.Value.Clone();
                    }

                    pendingToolUses.Add(new ToolUseContent
                    {
                        Id = callId,
                        Name = toolName,
                        Input = toolInput
                    });
                    break;

                case "tool_result":
                    var resultCallId = evt.Data.TryGetProperty("call_id", out var rci) ? rci.GetString() ?? "" : "";
                    var output = evt.Data.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                    var isError = evt.Data.TryGetProperty("is_error", out var ie) && ie.GetBoolean();

                    pendingToolResults.Add(new ToolResultContent
                    {
                        ToolUseId = resultCallId,
                        Content = output,
                        IsError = isError
                    });
                    break;
            }
        }

        // Flush any remaining tool messages
        FlushToolMessages(messages, ref pendingToolUses, ref pendingToolResults);

        config ??= new SessionConfig
        {
            Id = sessionId,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            FilePath = filePath
        };

        return new SessionInfo { Config = config, Messages = messages };
    }

    private static void FlushToolMessages(
        List<Message> messages,
        ref List<MessageContent> pendingToolUses,
        ref List<MessageContent> pendingToolResults)
    {
        if (pendingToolUses.Count > 0)
        {
            messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Content = pendingToolUses,
                Timestamp = DateTime.UtcNow.ToString("o")
            });
            pendingToolUses = new List<MessageContent>();
        }

        if (pendingToolResults.Count > 0)
        {
            messages.Add(new Message
            {
                Role = MessageRole.Tool,
                Content = pendingToolResults,
                Timestamp = DateTime.UtcNow.ToString("o")
            });
            pendingToolResults = new List<MessageContent>();
        }
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
                    var sessionConfig = JsonSerializer.Deserialize<SessionConfig>(configEl.GetRawText(), JsonOptions);
                    if (sessionConfig != null)
                        configs.Add(sessionConfig);
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
