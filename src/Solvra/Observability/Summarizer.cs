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

        // P6: Tool usage aggregation
        var toolCallEvents = events.Where(e => e.Type == "tool_call").ToList();
        var toolResultEvents = events.Where(e => e.Type == "tool_result").ToList();

        if (toolCallEvents.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Tool Usage");
            sb.AppendLine("| Tool | Calls | Errors |");
            sb.AppendLine("|------|-------|--------|");

            var toolGroups = toolCallEvents
                .GroupBy(e => e.Data.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "unknown" : "unknown");

            foreach (var group in toolGroups)
            {
                var callIds = group
                    .Select(e => e.Data.TryGetProperty("call_id", out var ci) ? ci.GetString() ?? "" : "")
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToHashSet();

                var errorCount = toolResultEvents.Count(e =>
                {
                    var matchId = e.Data.TryGetProperty("call_id", out var ci) ? ci.GetString() ?? "" : "";
                    var isErr = e.Data.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
                    return callIds.Contains(matchId) && isErr;
                });

                sb.AppendLine($"| {group.Key} | {group.Count()} | {errorCount} |");
            }
        }

        // P6: LLM token totals from session_end usage
        var endEvent = events.LastOrDefault(e => e.Type == "session_end");
        if (endEvent != null && endEvent.Data.ValueKind == JsonValueKind.Object)
        {
            if (endEvent.Data.TryGetProperty("usage", out var usage))
            {
                sb.AppendLine();
                sb.AppendLine("## LLM Usage");
                if (usage.TryGetProperty("input", out var inp))
                    sb.AppendLine($"- **Input Tokens:** {inp.GetInt32():N0}");
                if (usage.TryGetProperty("output", out var outp))
                    sb.AppendLine($"- **Output Tokens:** {outp.GetInt32():N0}");
            }
        }

        // P6: Error details
        var errorResults = toolResultEvents
            .Where(e => e.Data.TryGetProperty("is_error", out var ie) && ie.GetBoolean())
            .ToList();

        if (errorResults.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Errors");
            foreach (var err in errorResults)
            {
                var callId = err.Data.TryGetProperty("call_id", out var ci) ? ci.GetString() ?? "" : "";
                var output = err.Data.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                sb.AppendLine($"- `{callId}`: {Truncate(output, 200)}");
            }
        }

        // Final output from session_end
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
