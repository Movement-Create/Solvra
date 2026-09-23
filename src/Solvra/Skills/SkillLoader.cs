#nullable enable

using System.Text.RegularExpressions;

namespace Solvra.Skills;

public class SkillLoader
{
    private readonly string _skillsDir;
    private Dictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private long _lastScanTicks;
    private static readonly long RescanIntervalTicks = TimeSpan.FromSeconds(30).Ticks;

    public SkillLoader(string skillsDir)
    {
        _skillsDir = skillsDir;
    }

    public async Task<IReadOnlyList<SkillDefinition>> GetAllSkillsAsync()
    {
        await ScanIfStaleAsync();
        await _reloadLock.WaitAsync();
        try { return _skills.Values.ToList(); }
        finally { _reloadLock.Release(); }
    }

    public async Task<List<SkillDefinition>> GetRelevantSkillsAsync(string prompt)
    {
        await ScanIfStaleAsync();

        var lowerPrompt = prompt.ToLowerInvariant();
        var results = new List<SkillDefinition>();
        IReadOnlyList<SkillDefinition> snapshot;
        await _reloadLock.WaitAsync();
        try { snapshot = _skills.Values.ToList(); }
        finally { _reloadLock.Release(); }

        foreach (var skill in snapshot)
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

        try
        {
            await ReloadAsync();
        }
        catch
        {
            // Periodic discovery stays available on the last validated snapshot.
            // Explicit Reload/ReloadAsync calls surface the validation error.
            _lastScanTicks = now;
        }
    }

    /// <summary>
    /// Build and validate a complete skill snapshot before swapping it into use.
    /// The previous snapshot remains active if any file cannot be read or duplicate
    /// skill names make the candidate ambiguous.
    /// </summary>
    public async Task<SkillReloadDiff> ReloadAsync()
    {
        var candidate = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(_skillsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(_skillsDir).OrderBy(path => path, StringComparer.Ordinal))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillFile)) continue;
                var skill = await LoadSkillFileAsync(skillFile, Path.GetFileName(dir));
                if (string.IsNullOrWhiteSpace(skill.Name))
                    throw new InvalidDataException($"Skill in {skillFile} has an empty name.");
                if (!candidate.TryAdd(skill.Name, skill))
                    throw new InvalidDataException($"Duplicate skill name \"{skill.Name}\".");
            }
        }

        await _reloadLock.WaitAsync();
        try
        {
            var added = candidate.Keys.Except(_skills.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            var removed = _skills.Keys.Except(candidate.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            var changed = candidate.Keys.Intersect(_skills.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(name => !SkillEquals(candidate[name], _skills[name]))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            _skills = candidate;
            _lastScanTicks = DateTime.UtcNow.Ticks;
            return new SkillReloadDiff(added, removed, changed);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public SkillReloadDiff Reload() => ReloadAsync().GetAwaiter().GetResult();

    private static bool SkillEquals(SkillDefinition left, SkillDefinition right) =>
        left.Name == right.Name &&
        left.Description == right.Description &&
        left.TriggerPatterns.SequenceEqual(right.TriggerPatterns) &&
        left.AlwaysInject == right.AlwaysInject &&
        left.ToolsRequired.SequenceEqual(right.ToolsRequired) &&
        left.Content == right.Content &&
        left.FilePath == right.FilePath;

    private static async Task<SkillDefinition> LoadSkillFileAsync(string path, string dirName)
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

public sealed record SkillReloadDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed);
