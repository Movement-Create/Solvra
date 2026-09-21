#nullable enable

using System.Text.Json;
using System.Text.RegularExpressions;
using Solvra.Security;

namespace Solvra.Tools;

public class GlobTool : ToolBase
{
    public override string Name => "glob";
    private const int DefaultMaxResults = 500;

    public override string Description =>
        "Find files by glob pattern, newest first. \"**\" matches any number of directories; a pattern without a " +
        "slash (e.g. \"*.cs\") matches file names at any depth; {a,b} alternatives are supported. " +
        "Skips .git, node_modules, bin/obj and .gitignore'd paths.";

    public override PermissionLevel PermissionLevel => PermissionLevel.Read;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            pattern = new { type = "string", description = "Glob pattern (e.g. **/*.cs, src/*.{ts,tsx})" },
            path = new { type = "string", description = "Base directory to search (default: working directory)" },
            max_results = new { type = "integer", description = $"Maximum paths to return (default {DefaultMaxResults})" }
        },
        required = new[] { "pattern" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var pattern = GetString(input, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return new ToolExecuteResult("Error: pattern is required", true);
        var basePath = GetOptionalString(input, "path") is { Length: > 0 } p ? p : context.Cwd;
        var maxResults = Math.Clamp(GetOptionalInt(input, "max_results") ?? DefaultMaxResults, 1, 5000);

        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(context.Cwd, basePath);

        if (!Directory.Exists(basePath))
            return new ToolExecuteResult($"Error: directory not found: {basePath}", true);

        var regexes = ExpandBraces(pattern).Select(GlobToRegex).ToList();
        var matches = new List<(string Rel, DateTime Mtime)>();

        await Task.Run(() =>
        {
            foreach (var (full, rel) in new FileWalker(basePath).Files(ct: ct))
            {
                if (!regexes.Any(r => Matches(r, pattern, rel))) continue;
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(full); } catch { mtime = DateTime.MinValue; }
                matches.Add((rel, mtime));
            }
        }, ct);

        if (matches.Count == 0)
            return new ToolExecuteResult($"No files matched {pattern} under {basePath}.", false);

        var ordered = matches.OrderByDescending(m => m.Mtime).ThenBy(m => m.Rel, StringComparer.Ordinal).ToList();
        var text = string.Join('\n', ordered.Take(maxResults).Select(m => m.Rel));
        if (ordered.Count > maxResults)
            text += $"\n[{ordered.Count - maxResults} more files not shown; narrow the pattern.]";
        return new ToolExecuteResult(text, false);
    }

    /// <summary>
    /// A pattern containing '/' is matched against the whole relative path; one without '/'
    /// is matched against the file name (gitignore-style), so "*.cs" finds files at any depth.
    /// </summary>
    internal static bool Matches(Regex regex, string pattern, string relPath)
    {
        var normalized = relPath.Replace('\\', '/');
        if (pattern.Contains('/')) return regex.IsMatch(normalized);
        return regex.IsMatch(Path.GetFileName(normalized)) || regex.IsMatch(normalized);
    }

    /// <summary>Expand one level of {a,b,c} alternatives (nested braces are expanded recursively).</summary>
    internal static IEnumerable<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{');
        var close = open < 0 ? -1 : pattern.IndexOf('}', open);
        if (open < 0 || close < 0) return [pattern];
        var head = pattern[..open];
        var tail = pattern[(close + 1)..];
        return pattern[(open + 1)..close].Split(',').SelectMany(alt => ExpandBraces(head + alt + tail));
    }

    public static Regex GlobToRegex(string pattern)
    {
        var regexStr = "";
        var i = 0;

        while (i < pattern.Length)
        {
            var ch = pattern[i];

            if (ch == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                regexStr += ".*";
                i += 2;
                if (i < pattern.Length && pattern[i] == '/')
                    i++;
            }
            else if (ch == '*')
            {
                regexStr += "[^/]*";
                i++;
            }
            else if (ch == '?')
            {
                regexStr += "[^/]";
                i++;
            }
            else if (ch == '.')
            {
                regexStr += @"\.";
                i++;
            }
            else if (ch == '/')
            {
                regexStr += @"[/\\]";
                i++;
            }
            else if (ch == '[')
            {
                var end = pattern.IndexOf(']', i);
                if (end == -1)
                {
                    regexStr += @"\[";
                    i++;
                }
                else
                {
                    regexStr += pattern[i..(end + 1)];
                    i = end + 1;
                }
            }
            else
            {
                regexStr += Regex.Escape(ch.ToString());
                i++;
            }
        }

        return new Regex("^" + regexStr + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
