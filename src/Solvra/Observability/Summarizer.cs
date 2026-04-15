#nullable enable

using System.Text;
using System.Text.Json;

namespace Solvra.Observability;

/// <summary>
/// Reads a session JSONL file and produces a .summary.md file
/// with session metadata, turn-by-turn summary, and final output.
/// </summary>
public static class Summarizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static async Task<string> SummarizeSessionAsync(string sessionPath)
    {
        if (!File.Exists(sessionPath))
            throw new FileNotFoundException($"Session file not found: {sessionPath}");

        var lines = await File.ReadAllLinesAsync(sessionPath);
        var events = new List<SessionEventRecord>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                var data = root.TryGetProperty("data", out var d) ? d : default;
                events.Add(new SessionEventRecord(type, timestamp, data));
            }
            catch
            {
                // Skip malformed lines
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Session Summary");
        sb.AppendLine();

        // Session metadata from session_start event
        var startEvent = events.FirstOrDefault(e => e.Type == "session_start");
        if (startEvent != null && startEvent.Data.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("## Metadata");
            if (startEvent.Data.TryGetProperty("config", out var config))
            {
                if (config.TryGetProperty("model", out var model))
                    sb.AppendLine($"- **Model:** {model.GetString()}");
                if (config.TryGetProperty("provider", out var provider))
                    sb.AppendLine($"- **Provider:** {provider.GetString()}");
                if (config.TryGetProperty("id", out var id))
                    sb.AppendLine($"- **Session ID:** {id.GetString()}");
                if (config.TryGetProperty("created_at", out var created))
                    sb.AppendLine($"- **Created:** {created.GetString()}");
            }
            sb.AppendLine();
        }

        // Turn-by-turn summary
        sb.AppendLine("## Turns");
        sb.AppendLine();

        var turnNum = 0;
        foreach (var evt in events)
        {
            switch (evt.Type)
            {
                case "user_message":
                    turnNum++;
                    var userContent = evt.Data.TryGetProperty("content", out var uc) ? uc.GetString() ?? "" : "";
                    sb.AppendLine($"### Turn {turnNum}");
                    sb.AppendLine($"**User:** {Truncate(userContent, 200)}");
                    sb.AppendLine();
                    break;

                case "assistant_message":
                    var assistantContent = evt.Data.TryGetProperty("content", out var ac) ? ac.GetString() ?? "" : "";
                    sb.AppendLine($"**Assistant:** {Truncate(assistantContent, 200)}");
                    sb.AppendLine();
                    break;

                case "tool_call":
                    var toolName = evt.Data.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
                    sb.AppendLine($"- Tool call: `{toolName}`");
                    break;

                case "tool_result":
                    var isError = evt.Data.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
                    var output = evt.Data.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                    var status = isError ? "ERROR" : "OK";
                    sb.AppendLine($"- Tool result: [{status}] {Truncate(output, 100)}");
                    break;
            }
        }

        // Final output from session_end
        var endEvent = events.LastOrDefault(e => e.Type == "session_end");
        if (endEvent != null && endEvent.Data.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine();
            sb.AppendLine("## Result");
            if (endEvent.Data.TryGetProperty("text", out var text))
                sb.AppendLine($"**Final Output:** {Truncate(text.GetString() ?? "", 500)}");
            if (endEvent.Data.TryGetProperty("turns", out var turns))
                sb.AppendLine($"- **Turns:** {turns.GetInt32()}");
            if (endEvent.Data.TryGetProperty("cost_usd", out var cost))
                sb.AppendLine($"- **Cost:** ${cost.GetDecimal():F4}");
            if (endEvent.Data.TryGetProperty("stop_reason", out var reason))
                sb.AppendLine($"- **Stop Reason:** {reason.GetString()}");
        }

        var summary = sb.ToString();

        // Write summary file
        var summaryPath = Path.ChangeExtension(sessionPath, ".summary.md");
        await File.WriteAllTextAsync(summaryPath, summary);

        return summary;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    private record SessionEventRecord(string Type, string Timestamp, JsonElement Data);
}
