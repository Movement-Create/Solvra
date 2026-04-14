#nullable enable

using System.Text.RegularExpressions;

namespace Solvra.Skills;

public class SkillLoader
{
    private readonly string _skillsDir;
    private readonly Dictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);
    private long _lastScanTicks;
    private static readonly long RescanIntervalTicks = TimeSpan.FromSeconds(30).Ticks;

    public SkillLoader(string skillsDir)
    {
        _skillsDir = skillsDir;
    }

    public async Task<IReadOnlyList<SkillDefinition>> GetAllSkillsAsync()
    {
        await ScanIfStaleAsync();
        return _skills.Values.ToList();
    }

    public async Task<List<SkillDefinition>> GetRelevantSkillsAsync(string prompt)
    {
        await ScanIfStaleAsync();

        var lowerPrompt = prompt.ToLowerInvariant();
        var results = new List<SkillDefinition>();

        foreach (var skill in _skills.Values)
        {
            if (skill.AlwaysInject)
            {
                results.Add(skill);
                continue;
            }

            if (MatchesTriggerPatterns(skill.TriggerPatterns, lowerPrompt))
            {
                results.Add(skill);
            }
        }

        return results;
    }

    /// <summary>
    /// Word-boundary regex matching for trigger patterns.
    /// Ported from TypeScript: escape the pattern, wrap with \b...\b, case-insensitive.
    /// When the pattern starts/ends with a non-word character, use lookahead/lookbehind
    /// for whitespace or string boundary instead of \b.
    /// </summary>
    public static bool MatchesTriggerPatterns(List<string> patterns, string lowerPrompt)
    {
        foreach (var pattern in patterns)
        {
            var escaped = Regex.Escape(pattern.ToLowerInvariant());

            // Determine boundary assertions: \b works between word/non-word chars.
            // If the pattern edge is a non-word char, use whitespace/boundary assertion instead.
            var startBoundary = char.IsLetterOrDigit(pattern[0]) ? @"\b" : @"(?<=\s|^)";
            var endBoundary = char.IsLetterOrDigit(pattern[^1]) ? @"\b" : @"(?=\s|$)";

            var regex = new Regex($"{startBoundary}{escaped}{endBoundary}", RegexOptions.IgnoreCase);
            if (regex.IsMatch(lowerPrompt))
                return true;
        }
        return false;
    }

    private async Task ScanIfStaleAsync()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - _lastScanTicks < RescanIntervalTicks && _skills.Count > 0)
            return;

        await ScanSkillsDirAsync();
        _lastScanTicks = now;
    }

    private async Task ScanSkillsDirAsync()
    {
        if (!Directory.Exists(_skillsDir)) return;

        foreach (var dir in Directory.EnumerateDirectories(_skillsDir))
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;

            try
            {
                var skill = await LoadSkillFileAsync(skillFile, Path.GetFileName(dir));
                if (skill != null)
                    _skills[skill.Name] = skill;
            }
            catch { }
        }
    }

    private static async Task<SkillDefinition?> LoadSkillFileAsync(string path, string dirName)
    {
        var content = await File.ReadAllTextAsync(path);
        var (frontmatter, body) = ParseFrontmatter(content);

        var name = frontmatter.GetValueOrDefault("name") ?? dirName;
        var description = frontmatter.GetValueOrDefault("description") ?? "";

        var triggerPatterns = ParseArrayValue(frontmatter.GetValueOrDefault("trigger_patterns") ?? "");
        var toolsRequired = ParseArrayValue(frontmatter.GetValueOrDefault("tools_required") ?? "");
        var alwaysInject = string.Equals(frontmatter.GetValueOrDefault("always_inject"), "true", StringComparison.OrdinalIgnoreCase);

        return new SkillDefinition(
            Name: name,
            Description: description,
            TriggerPatterns: triggerPatterns,
            AlwaysInject: alwaysInject,
            ToolsRequired: toolsRequired,
            Content: body,
            FilePath: path);
    }

    public static (Dictionary<string, string> Frontmatter, string Body) ParseFrontmatter(string content)
    {
        var match = Regex.Match(content, @"^---\s*\n([\s\S]*?)\n---\s*\n([\s\S]*)$");
        if (!match.Success)
            return (new Dictionary<string, string>(), content);

        var yamlSection = match.Groups[1].Value;
        var body = match.Groups[2].Value;
        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in yamlSection.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx == -1) continue;

            var key = trimmed[..colonIdx].Trim();
            var rawValue = trimmed[(colonIdx + 1)..].Trim();

            // Strip quotes
            if ((rawValue.StartsWith('"') && rawValue.EndsWith('"')) ||
                (rawValue.StartsWith('\'') && rawValue.EndsWith('\'')))
            {
                rawValue = rawValue[1..^1];
            }

            frontmatter[key] = rawValue;
        }

        return (frontmatter, body);
    }

    private static List<string> ParseArrayValue(string value)
    {
        if (!value.StartsWith('[') || !value.EndsWith(']'))
            return string.IsNullOrEmpty(value) ? new List<string>() : new List<string> { value };

        return value[1..^1]
            .Split(',')
            .Select(s => s.Trim().Trim('"', '\''))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }
}
