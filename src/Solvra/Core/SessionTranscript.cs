#nullable enable

using System.Text.Json;
using Solvra.Models;

namespace Solvra.Core;

/// <summary>A stored session flattened for UIs (<c>solvra session show --json</c>).</summary>
public static class SessionTranscript
{
    public sealed record Tool(string Id, string Name, Dictionary<string, JsonElement> Input, string? Output, bool? Ok);
    public sealed record Entry(string Role, string Text, IReadOnlyList<Tool> Tools);

    public static object Build(SessionInfo info)
    {
        // Tool results arrive in a later message; attach them to the call that made them.
        var results = info.Messages.SelectMany(m => m.Content.OfType<ToolResultContent>())
            .GroupBy(r => r.ToolUseId).ToDictionary(g => g.Key, g => g.Last());
        var entries = new List<Entry>();
        foreach (var m in info.Messages)
        {
            if (m.Role == MessageRole.Tool) continue;
            if (m.Role == MessageRole.User && m.Content.All(c => c is ToolResultContent)) continue;
            var text = string.Join("", m.Content.OfType<TextContent>().Select(t => t.Text));
            var tools = m.Content.OfType<ToolUseContent>()
                .Select(t => results.TryGetValue(t.Id, out var r)
                    ? new Tool(t.Id, t.Name, t.Input, r.Content, !r.IsError)
                    : new Tool(t.Id, t.Name, t.Input, null, null))
                .ToList();
            if (text.Length == 0 && tools.Count == 0) continue;
            entries.Add(new Entry(m.Role == MessageRole.User ? "user" : "assistant", text, tools));
        }
        return new
        {
            id = info.Config.Id,
            title = SessionTitles.Display(info.Config.Title, entries.FirstOrDefault(e => e.Role == "user")?.Text),
            model = info.Config.Model,
            provider = info.Config.Provider,
            created_at = info.Config.CreatedAt,
            messages = entries,
        };
    }
}
