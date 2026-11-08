using System.Text;
using System.Text.Json;

namespace Solvra.Telemetry;

// Positive allowlists on BOTH sides of the public endpoint. No free text, model responses,
// paths, commands, tool arguments, prompts, exception messages or credentials leave in cleartext.
internal static class Metadata
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "agent.session", "agent.turn", "llm.call", "tool.execute", "SessionStart", "SessionEnd",
        "model_call_started", "model_call_completed", "model_call_failed", "tool_call_started",
        "tool_call_completed", "tool_execution_metrics", "operation_interrupted", "run_start", "run_end",
        "user_message", "assistant_message", "assistant_turn", "tool_result", "session_start", "system_prompt"
    };
    private static readonly HashSet<string> Numbers = new(StringComparer.Ordinal)
    {
        "turn", "turns", "duration_ms", "input_tokens", "output_tokens", "cache_read_tokens",
        "cache_write_tokens", "total_tokens", "cost_usd", "decisions", "verifications", "bytes",
        "output_bytes", "exit_code", "is_error", "success"
    };
    internal static object[] Extract(byte[] bytes, string device)
    {
        var records = new List<object>();
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = String(r, "type");
                var name = String(r, "event_type") ?? String(r, "name") ?? type;
                if (name is null || !Names.Contains(name)) continue;
                if (!DateTimeOffset.TryParse(String(r, "timestamp"), out var timestamp)) continue;
                DateTimeOffset.TryParse(String(r, "start_time"), out var start);
                var attrs = new Dictionary<string, object>();
                foreach (var field in new[] { "attributes", "data" })
                    if (r.TryGetProperty(field, out var obj) && obj.ValueKind == JsonValueKind.Object)
                        foreach (var p in obj.EnumerateObject())
                            if (Numbers.Contains(p.Name) && p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                                attrs[p.Name] = p.Value.Clone();
                records.Add(new
                {
                    name, kind = type == "span_end" ? "span" : "log", time = timestamp.ToUnixTimeMilliseconds(),
                    start = start == default ? timestamp.ToUnixTimeMilliseconds() : start.ToUnixTimeMilliseconds(),
                    trace = Hex(String(r, "trace_id"), 16, 32), span = Hex(String(r, "span_id"), 16),
                    parent = Hex(String(r, "parent_span_id"), 16), numbers = attrs
                });
            }
            catch (JsonException) { /* Invalid source retained verbatim in encrypted archive. */ }
        }
        return records.ToArray();
    }
    private static string? String(JsonElement r, string key) => r.ValueKind == JsonValueKind.Object &&
        r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? Hex(string? s, params int[] lengths) => s is not null && lengths.Contains(s.Length) &&
        s.All(Uri.IsHexDigit) && s.Any(c => c != '0') ? s.ToLowerInvariant() : null;
}
