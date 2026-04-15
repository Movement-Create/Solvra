#nullable enable

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Solvra.Memory;

public record Lesson(string Date, List<string> Tags, string Content);

public class MemoryManager
{
    private const int MaxLessonsInjected = 5;
    private readonly string _memoryDir;

    public MemoryManager(string memoryDir)
    {
        _memoryDir = memoryDir;
    }

    public async Task<string> SearchAsync(string query)
    {
        var results = new List<string>();

        // Search facts.md
        var factsPath = Path.Combine(_memoryDir, "facts.md");
        if (File.Exists(factsPath))
        {
            var factsContent = await File.ReadAllTextAsync(factsPath);
            var factsMatches = SearchContent(factsContent, query);
            if (factsMatches.Count > 0)
            {
                results.Add("=== Facts ===");
                results.AddRange(factsMatches);
            }
        }

        // Search lessons.md
        var lessonsPath = Path.Combine(_memoryDir, "lessons.md");
        if (File.Exists(lessonsPath))
        {
            var lessonsContent = await File.ReadAllTextAsync(lessonsPath);
            var lessonMatches = SearchContent(lessonsContent, query);
            if (lessonMatches.Count > 0)
            {
                results.Add("=== Lessons ===");
                results.AddRange(lessonMatches);
            }
        }

        // Search recent daily logs — Fix 5d: use memory/ not memory/daily/
        var recentLogs = Directory.Exists(_memoryDir)
            ? Directory.GetFiles(_memoryDir, "????-??-??.md")
                .OrderByDescending(f => f)
                .Take(7)
            : Enumerable.Empty<string>();

        foreach (var logFile in recentLogs)
        {
            var logContent = await File.ReadAllTextAsync(logFile);
            var logMatches = SearchContent(logContent, query);
            if (logMatches.Count > 0)
            {
                results.Add($"=== {Path.GetFileNameWithoutExtension(logFile)} ===");
                results.AddRange(logMatches);
            }
        }

        return string.Join('\n', results);
    }

    public async Task AppendFactAsync(string content)
    {
        Directory.CreateDirectory(_memoryDir);
        var path = Path.Combine(_memoryDir, "facts.md");
        var entry = $"\n## {DateTime.UtcNow:yyyy-MM-dd}\n{content}\n";
        await File.AppendAllTextAsync(path, entry);
    }

    public async Task AppendLessonAsync(string content, string[]? tags = null)
    {
        Directory.CreateDirectory(_memoryDir);
        var path = Path.Combine(_memoryDir, "lessons.md");
        var tagsStr = tags?.Length > 0 ? $" [{string.Join(", ", tags)}]" : "";
        var entry = $"\n## {DateTime.UtcNow:yyyy-MM-dd}{tagsStr}\n{content}\n";
        await File.AppendAllTextAsync(path, entry);
    }

    /// <summary>
    /// Fix 5a: Atomic overwrite of lessons with .bak backup.
    /// </summary>
    public async Task WriteLessonsAsync(List<Lesson> lessons)
    {
        Directory.CreateDirectory(_memoryDir);
        var path = Path.Combine(_memoryDir, "lessons.md");
        var backup = path + ".bak";

        if (File.Exists(path))
            File.Copy(path, backup, overwrite: true);

        var content = string.Join("\n\n", lessons.Select(l =>
            $"## {l.Date} [{string.Join(", ", l.Tags)}]\n{l.Content}"));

        await File.WriteAllTextAsync(path, content);
    }

    /// <summary>
    /// Fix 5b: Load facts.md content.
    /// </summary>
    public async Task<string?> LoadFactsAsync()
    {
        var path = Path.Combine(_memoryDir, "facts.md");
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
    }

    /// <summary>
    /// Fix 5c: Log a conversation to the daily log file.
    /// Fix 5d: Uses memory/YYYY-MM-DD.md not memory/daily/YYYY-MM-DD.md.
    /// P7: Also rebuilds keyword index after logging.
    /// </summary>
    public async Task LogConversationAsync(string prompt, string response, string sessionId)
    {
        Directory.CreateDirectory(_memoryDir);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var logPath = Path.Combine(_memoryDir, $"{today}.md");
        var promptSnippet = prompt.Length > 200 ? prompt[..200] : prompt;
        var responseSnippet = response.Length > 200 ? response[..200] : response;
        var snippet = $"## {DateTime.UtcNow:HH:mm} \u2014 Session {sessionId}\n**User:** {promptSnippet}\n**Agent:** {responseSnippet}\n\n";
        await File.AppendAllTextAsync(logPath, snippet);

        // P7: Rebuild index after logging
        await RebuildIndexAsync();
    }

    /// <summary>
    /// P7: Extract facts from text using simple heuristic.
    /// Lines starting with "- " or "* " are treated as facts.
    /// </summary>
    public Task<List<string>> ExtractFactsAsync(string text)
    {
        var facts = text.Split('\n')
            .Where(l => l.TrimStart().StartsWith("- ") || l.TrimStart().StartsWith("* "))
            .Select(l => l.TrimStart().TrimStart('-', '*').Trim())
            .Where(l => l.Length > 10)
            .ToList();
        return Task.FromResult(facts);
    }

    /// <summary>
    /// Fix 5e: Rebuild keyword index from all .md files in memory/.
    /// Writes memory/index.json.
    /// </summary>
    public async Task RebuildIndexAsync()
    {
        if (!Directory.Exists(_memoryDir)) return;

        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(_memoryDir, "*.md"))
        {
            var filename = Path.GetFileName(file);
            if (filename == "index.json") continue;

            var content = await File.ReadAllTextAsync(file);
            var words = Regex.Split(content.ToLowerInvariant(), @"[^a-z0-9_./-]+")
                .Where(w => w.Length >= 3)
                .Distinct();

            foreach (var word in words)
            {
                if (!index.TryGetValue(word, out var files))
                {
                    files = new List<string>();
                    index[word] = files;
                }
                if (!files.Contains(filename))
                    files.Add(filename);
            }
        }

        var indexPath = Path.Combine(_memoryDir, "index.json");
        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(indexPath, json);
    }

    public async Task<List<Lesson>> GetRelevantLessonsAsync(string prompt)
    {
        var all = await ParseLessonsAsync();
        if (all.Count == 0) return new List<Lesson>();

        var lowerPrompt = prompt.ToLowerInvariant();

        // Extract prompt words (min 3 chars)
        var promptWords = new HashSet<string>(
            Regex.Split(lowerPrompt, @"[^a-z0-9_./-]+")
                .Where(w => w.Length >= 3),
            StringComparer.OrdinalIgnoreCase);

        var scored = all.Select(lesson =>
        {
            var score = 0;

            // Signal 1: tag substring match (weight = 3)
            foreach (var tag in lesson.Tags)
            {
                if (lowerPrompt.Contains(tag.ToLowerInvariant()))
                    score += 3;
            }

            // Signal 2: content word overlap (weight = 1, max 1 per lesson)
            var contentWords = Regex.Split(lesson.Content.ToLowerInvariant(), @"[^a-z0-9_./-]+")
                .Where(w => w.Length >= 3);

            foreach (var w in contentWords)
            {
                if (promptWords.Contains(w))
                {
                    score += 1;
                    break;
                }
            }

            return (Lesson: lesson, Score: score);
        });

        return scored
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .Take(MaxLessonsInjected)
            .Select(s => s.Lesson)
            .ToList();
    }

    public async Task<List<Lesson>> ParseLessonsAsync()
    {
        var path = Path.Combine(_memoryDir, "lessons.md");
        if (!File.Exists(path)) return new List<Lesson>();

        var content = await File.ReadAllTextAsync(path);
        return ParseLessons(content);
    }

    public static List<Lesson> ParseLessons(string content)
    {
        var lessons = new List<Lesson>();
        var pattern = new Regex(@"^## (\d{4}-\d{2}-\d{2})(?: \[([^\]]*)\])?\s*$", RegexOptions.Multiline);
        var matches = pattern.Matches(content);

        for (var i = 0; i < matches.Count; i++)
        {
            var date = matches[i].Groups[1].Value;
            var tagsStr = matches[i].Groups[2].Value;
            var tags = string.IsNullOrEmpty(tagsStr)
                ? new List<string>()
                : tagsStr.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();

            var startIdx = matches[i].Index + matches[i].Length;
            var endIdx = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
            var body = content[startIdx..endIdx].Trim();

            if (!string.IsNullOrEmpty(body))
                lessons.Add(new Lesson(date, tags, body));
        }

        return lessons;
    }

    public async Task AppendToDailyLogAsync(string content, string? sessionId = null)
    {
        // Fix 5d: Use memory/YYYY-MM-DD.md not memory/daily/YYYY-MM-DD.md
        Directory.CreateDirectory(_memoryDir);
        var path = Path.Combine(_memoryDir, $"{DateTime.UtcNow:yyyy-MM-dd}.md");
        var prefix = sessionId != null ? $"[{sessionId}] " : "";
        var entry = $"\n### {DateTime.UtcNow:HH:mm:ss} {prefix}\n{content}\n";
        await File.AppendAllTextAsync(path, entry);
    }

    private static List<string> SearchContent(string content, string query)
    {
        var results = new List<string>();
        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Split by ## headers
        var sections = Regex.Split(content, @"(?=^## )", RegexOptions.Multiline);

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;
            var lowerSection = section.ToLowerInvariant();

            if (queryWords.Any(word => lowerSection.Contains(word)))
            {
                results.Add(section.Trim());
            }
        }

        return results;
    }
}
