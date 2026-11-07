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

/// <summary>Integrity and provenance fields stored next to a tool result (never sent to the model).</summary>
public record ToolResultMeta(int OutputBytes, string OutputSha256, bool Truncated, string? SpillPath, int? ExitCode, IReadOnlyList<object>? Records);

/// <summary>One row of <c>session list</c>: the same title rule everywhere (CLI, --json, VS Code).</summary>
public sealed record SessionSummary(string Id, string Title, string CreatedAt, string UpdatedAt, string Model, string Provider);

/// <summary>How session titles are made and shown.</summary>
public static class SessionTitles
{
    public const int MaxLength = 60;
    public const string Untitled = "Untitled";

    /// <summary>A title from free text: first non-blank line, whitespace collapsed, at most 60 characters.</summary>
    public static string? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ");
        if (line.Length == 0) return null;
        return line.Length > MaxLength ? line[..(MaxLength - 1)].TrimEnd() + "…" : line;
    }

    /// <summary>The stored title, else one derived from the first user message, else "Untitled".</summary>
    public static string Display(string? storedTitle, string? firstUserText) =>
        FromText(storedTitle) ?? FromText(firstUserText) ?? Untitled;
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
        _sessionsDir = sessionsDir ?? Solvra.Config.SolvraPaths.SessionsDir;
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

    // Tool calls in a parallel batch log their results at the same time. Two concurrent appends open
    // the file at the same end offset and the second overwrites the first, which loses a tool_result
    // and makes the session unresumable ("No tool output found for function call"). One writer at a time.
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public async Task AppendEventAsync(SessionConfig session, SessionEvent evt)
    {
        if (string.IsNullOrEmpty(session.FilePath)) return;
        var line = JsonSerializer.Serialize(evt, JsonOptions) + "\n";
        await WriteLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(session.FilePath, line);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task LogUserMessageAsync(SessionConfig session, string content)
        => await LogUserMessageAsync(session, Message.FromText(MessageRole.User, content));

    public async Task LogUserMessageAsync(SessionConfig session, Message message)
    {
        var data = JsonSerializer.SerializeToElement(new { content = message.Content });
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
    /// Log a complete assistant turn (text, reasoning and tool calls) in order, so resume
    /// rebuilds exactly what the model saw. Replaces separate tool_call events.
    /// </summary>
    public async Task LogAssistantTurnAsync(SessionConfig session, Message message)
    {
        // Default options on both sides: content blocks round-trip through their own attributes.
        var data = JsonSerializer.SerializeToElement(new { content = message.Content });
        await AppendEventAsync(session, new SessionEvent
        {
            Type = "assistant_turn",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = data
        });
    }

    /// <summary>Session ids are file names: letters, digits, '.', '_' and '-' only.</summary>
    public static bool IsValidSessionId(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && sessionId.Length <= 128 && !sessionId.Contains("..") &&
        sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private string SessionPath(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
            throw new ArgumentException($"Invalid session id: {sessionId}");
        return Path.Combine(_sessionsDir, $"{sessionId}.jsonl");
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
    public async Task AppendToolResultAsync(SessionConfig session, string callId, string output, bool isError, ToolResultMeta? meta = null)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            call_id = callId,
            output,
            is_error = isError,
            output_bytes = meta?.OutputBytes,
            output_sha256 = meta?.OutputSha256,
            truncated = meta?.Truncated,
            spill_path = meta?.SpillPath,
            exit_code = meta?.ExitCode,
            records = meta?.Records
        }, JsonOptions);

        await AppendEventAsync(session, new SessionEvent
        {
            Type = "tool_result",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = data
        });
    }

    /// <summary>Start-of-run identity: trace id, agent version/revision, instruction files and hashes.</summary>
    public async Task LogRunStartAsync(SessionConfig session, object data)
    {
        await AppendEventAsync(session, new SessionEvent
        {
            Type = "run_start",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = JsonSerializer.SerializeToElement(data, JsonOptions)
        });
    }

    /// <summary>The full system prompt, written once per session and again only when its hash changes.</summary>
    public async Task LogSystemPromptAsync(SessionConfig session, string sha256, string text)
    {
        await AppendEventAsync(session, new SessionEvent
        {
            Type = "system_prompt",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = JsonSerializer.SerializeToElement(new { sha256, text }, JsonOptions)
        });
    }

    /// <summary>End-of-run record: trace id, stop reason, usage, decisions, verifications and written files.</summary>
    public async Task LogRunEndAsync(SessionConfig session, object data)
    {
        await AppendEventAsync(session, new SessionEvent
        {
            Type = "run_end",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = JsonSerializer.SerializeToElement(data, JsonOptions)
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
        var filePath = SessionPath(sessionId);
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

            SessionEvent? evt;
            try { evt = JsonSerializer.Deserialize<SessionEvent>(line, JsonOptions); }
            catch (JsonException) { continue; } // a torn/corrupt line must not lose the whole session
            if (evt == null) continue;

            switch (evt.Type)
            {
                case "session_start":
                    if (evt.Data.TryGetProperty("config", out var configEl))
                        config = JsonSerializer.Deserialize<SessionConfig>(configEl.GetRawText(), JsonOptions);
                    break;

                case "session_title":
                    if (config != null && evt.Data.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                        config = config with { Title = titleEl.GetString() };
                    break;

                case "user_message":
                    // Flush any pending tool results before user message
                    FlushToolMessages(messages, ref pendingToolUses, ref pendingToolResults);

                    if (evt.Data.TryGetProperty("content", out var userContent))
                    {
                        if (userContent.ValueKind == JsonValueKind.Array)
                        {
                            try
                            {
                                var blocks = JsonSerializer.Deserialize<List<MessageContent>>(userContent.GetRawText());
                                if (blocks is { Count: > 0 })
                                    messages.Add(new Message { Role = MessageRole.User, Content = blocks, Timestamp = evt.Timestamp });
                            }
                            catch (JsonException) { }
                        }
                        else
                        {
                            messages.Add(Message.FromText(MessageRole.User, userContent.GetString() ?? ""));
                        }
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

                case "assistant_turn":
                    FlushToolMessages(messages, ref pendingToolUses, ref pendingToolResults);
                    if (evt.Data.TryGetProperty("content", out var turnContent) && turnContent.ValueKind == JsonValueKind.Array)
                    {
                        List<MessageContent>? blocks = null;
                        try { blocks = JsonSerializer.Deserialize<List<MessageContent>>(turnContent.GetRawText()); }
                        catch (JsonException) { }
                        if (blocks is { Count: > 0 })
                            messages.Add(new Message { Role = MessageRole.Assistant, Content = blocks, Timestamp = evt.Timestamp });
                    }
                    break;

                case "tool_call":
                    // Older session files: tool calls logged one by one.
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
                    var isError = evt.Data.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;

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
        // Keep appending to the file we actually loaded, even if the stored path was relative
        // to another working directory.
        config = config with { FilePath = filePath };

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

    /// <summary>Rename by appending a session_title event; existing lines are never rewritten.</summary>
    public async Task RenameAsync(string sessionId, string title)
    {
        var path = SessionPath(sessionId);
        if (!File.Exists(path)) throw new FileNotFoundException($"Session {sessionId} not found", path);
        var clean = SessionTitles.FromText(title) ?? throw new ArgumentException("Title must not be empty.", nameof(title));
        await AppendEventAsync(new SessionConfig { Id = sessionId, CreatedAt = "", FilePath = path }, new SessionEvent
        {
            Type = "session_title",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Data = JsonSerializer.SerializeToElement(new { title = clean }, JsonOptions)
        });
    }

    /// <summary>Sessions newest first, with display titles (renames applied, else the first user message).</summary>
    public async Task<IReadOnlyList<SessionSummary>> ListSummariesAsync()
    {
        if (!Directory.Exists(_sessionsDir)) return [];
        var rows = new List<SessionSummary>();
        foreach (var file in Directory.GetFiles(_sessionsDir, "*.jsonl"))
        {
            try
            {
                SessionConfig? config = null;
                string? renamed = null, firstUser = null;
                using var reader = new StreamReader(file);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    // Only three event types matter; skip the rest without parsing them.
                    var isStart = config == null && line.Contains("\"session_start\"", StringComparison.Ordinal);
                    var isTitle = line.StartsWith("{\"type\":\"session_title\"", StringComparison.Ordinal);
                    var isUser = firstUser == null && line.StartsWith("{\"type\":\"user_message\"", StringComparison.Ordinal);
                    if (!isStart && !isTitle && !isUser) continue;
                    SessionEvent? evt;
                    try { evt = JsonSerializer.Deserialize<SessionEvent>(line, JsonOptions); }
                    catch (JsonException) { continue; }
                    if (evt == null) continue;
                    if (evt.Type == "session_start" && evt.Data.TryGetProperty("config", out var c))
                        config = JsonSerializer.Deserialize<SessionConfig>(c.GetRawText(), JsonOptions);
                    else if (evt.Type == "session_title" && evt.Data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                        renamed = t.GetString();
                    else if (evt.Type == "user_message" && evt.Data.TryGetProperty("content", out var u))
                        firstUser = u.ValueKind == JsonValueKind.String ? u.GetString()
                            : u.ValueKind == JsonValueKind.Array ? u.EnumerateArray()
                                .Where(b => b.TryGetProperty("text", out _)).Select(b => b.GetProperty("text").GetString()).FirstOrDefault()
                            : null;
                }
                if (config == null) continue;
                rows.Add(new SessionSummary(config.Id, SessionTitles.Display(renamed ?? config.Title, firstUser), config.CreatedAt,
                    File.GetLastWriteTimeUtc(file).ToString("o"), config.Model, config.Provider));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* skip unreadable sessions */ }
        }
        return rows.OrderByDescending(r => r.UpdatedAt, StringComparer.Ordinal).ToList();
    }

    public Task DeleteAsync(string sessionId)
    {
        var filePath = SessionPath(sessionId);
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }
}
