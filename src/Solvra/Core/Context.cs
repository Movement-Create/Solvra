using Solvra.Models;

namespace Solvra.Core;

public static class Context
{
    private const int CharsPerToken = 4;
    private const double MicroCompactThreshold = 0.70;
    private const double SummarizeThreshold = 0.85;
    private const double TruncateThreshold = 0.95;
    private const int MicroCompactMaxChars = 500;
    private const int KeepFirst = 2;
    private const int KeepRecent = 30;

    private static readonly Dictionary<string, int> ModelContextLimits = new()
    {
        ["claude-3-5-sonnet-20241022"] = 200_000,
        ["claude-3-5-haiku-20241022"] = 200_000,
        ["claude-3-opus-20240229"] = 200_000,
        ["claude-sonnet-4-20250514"] = 200_000,
        ["claude-opus-4-6"] = 200_000,
        ["gpt-4o"] = 128_000,
        ["gpt-4o-mini"] = 128_000,
        ["o1-preview"] = 128_000,
        ["o1"] = 200_000,
        ["gemini-2.5-pro"] = 1_000_000,
        ["gemini-2.5-flash"] = 1_000_000,
        ["gemini-2.0-flash-lite"] = 1_000_000,
        ["llama3.1"] = 128_000,
        ["llama3.2"] = 128_000,
        ["llama3.1:70b"] = 128_000,
    };

    private const int DefaultContextLimit = 128_000;

    public static int EstimateTokens(string text) =>
        (int)Math.Ceiling((double)text.Length / CharsPerToken);

    public static int EstimateContextTokens(IReadOnlyList<Message> messages)
    {
        int total = 0;
        foreach (var msg in messages)
        {
            foreach (var block in msg.Content)
            {
                var text = block switch
                {
                    TextContent tc => tc.Text,
                    ToolUseContent tu => $"{tu.Name}({System.Text.Json.JsonSerializer.Serialize(tu.Input)})",
                    ToolResultContent tr => tr.Content,
                    _ => ""
                };
                total += EstimateTokens(text);
            }
        }
        return total;
    }

    public static int GetContextLimit(string model, string? provider = null)
    {
        // Exact match
        if (ModelContextLimits.TryGetValue(model, out var limit))
            return limit;

        // Prefix match: take first 2 dash-separated segments
        var parts = model.Split('-');
        if (parts.Length >= 2)
        {
            var prefix = $"{parts[0]}-{parts[1]}";
            foreach (var (key, value) in ModelContextLimits)
            {
                if (key.StartsWith(prefix))
                    return value;
            }
        }

        return DefaultContextLimit;
    }

    public static string AssembleContext(
        string? basePrompt,
        string? solvraMarkdown,
        IReadOnlyList<string>? skills,
        IReadOnlyList<string>? lessons,
        string? memoryFacts)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(basePrompt))
            parts.Add(basePrompt);

        if (!string.IsNullOrEmpty(solvraMarkdown))
            parts.Add(solvraMarkdown);

        if (skills is { Count: > 0 })
        {
            parts.Add("# Active Skills\n\n" + string.Join("\n\n---\n\n", skills));
        }

        if (lessons is { Count: > 0 })
        {
            parts.Add("# Lessons (relevant to this turn)\n\n" + string.Join("\n\n", lessons));
        }

        if (!string.IsNullOrEmpty(memoryFacts))
        {
            parts.Add("# Memory\n\n" + memoryFacts);
        }

        parts.Add(DefaultInstructions);

        return string.Join("\n\n", parts);
    }

    public static IReadOnlyList<Message> CompressContext(
        IReadOnlyList<Message> messages,
        string model,
        string? provider = null)
    {
        var contextLimit = GetContextLimit(model, provider);
        var estimatedTokens = EstimateContextTokens(messages);
        var ratio = (double)estimatedTokens / contextLimit;

        if (ratio < MicroCompactThreshold)
            return messages;

        if (ratio < SummarizeThreshold)
            return MicroCompact(messages);

        if (ratio < TruncateThreshold)
            return TruncateOld(messages, 0.5);

        return TruncateOld(messages, 0.8);
    }

    private static IReadOnlyList<Message> MicroCompact(IReadOnlyList<Message> messages)
    {
        var result = new List<Message>(messages.Count);
        foreach (var msg in messages)
        {
            if (msg.Role != MessageRole.Tool)
            {
                result.Add(msg);
                continue;
            }

            var newContent = new List<MessageContent>();
            foreach (var block in msg.Content)
            {
                if (block is ToolResultContent tr && tr.Content.Length > MicroCompactMaxChars)
                {
                    newContent.Add(tr with
                    {
                        Content = tr.Content[..MicroCompactMaxChars] +
                            $"...[compacted: {tr.Content.Length} chars total]"
                    });
                }
                else
                {
                    newContent.Add(block);
                }
            }
            result.Add(msg with { Content = newContent });
        }
        return result;
    }

    private static IReadOnlyList<Message> TruncateOld(IReadOnlyList<Message> messages, double fraction)
    {
        if (messages.Count <= KeepFirst + KeepRecent)
            return messages;

        var result = new List<Message>();

        // Keep first messages
        for (int i = 0; i < Math.Min(KeepFirst, messages.Count); i++)
            result.Add(messages[i]);

        // Insert compaction notice
        var pct = (int)(fraction * 100);
        result.Add(Message.FromText(MessageRole.System,
            $"[Context compacted: {pct}% of conversation history removed to fit context window]"));

        // Keep recent messages
        var recentStart = Math.Max(KeepFirst, messages.Count - KeepRecent);
        for (int i = recentStart; i < messages.Count; i++)
            result.Add(messages[i]);

        return result;
    }

    private const string DefaultInstructions = """
        # Agent Instructions

        You are an autonomous AI agent. Follow these guidelines:
        1. Think step-by-step before taking actions.
        2. Use tools to accomplish tasks. Prefer precise, targeted tool calls.
        3. Verify your work after making changes.
        4. If you encounter an error, diagnose the root cause before retrying.
        5. When done, provide a clear summary of what you accomplished.
        6. If you're unsure about something, say so rather than guessing.
        """;
}
