#nullable enable

using System.Text.Json;
using System.Text.RegularExpressions;
using Solvra.Security;

namespace Solvra.Tools;

public class GlobTool : ToolBase
{
    public override string Name => "glob";
    public override string Description => "Find files matching a glob pattern.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Read;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            pattern = new { type = "string", description = "Glob pattern (e.g., **/*.cs)" },
            path = new { type = "string", description = "Base directory to search" }
        },
        required = new[] { "pattern" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var pattern = GetString(input, "pattern");
        var basePath = GetOptionalString(input, "path") ?? context.Cwd;

        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(context.Cwd, basePath);

        if (!Directory.Exists(basePath))
            return new ToolExecuteResult($"Error: directory not found: {basePath}", true);

        var regex = GlobToRegex(pattern);
        var ignoreSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", ".vs", "__pycache__"
        };

        var results = new List<string>();
        await Task.Run(() => WalkDirectory(basePath, basePath, regex, ignoreSet, results), ct);

        results.Sort(StringComparer.OrdinalIgnoreCase);

        if (results.Count == 0)
            return new ToolExecuteResult("No files matched the pattern.", false);

        return new ToolExecuteResult(string.Join('\n', results), false);
    }

    private static void WalkDirectory(string root, string current, Regex pattern, HashSet<string> ignore, List<string> results)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var name = Path.GetFileName(entry);
                if (ignore.Contains(name)) continue;

                if (Directory.Exists(entry))
                {
                    WalkDirectory(root, entry, pattern, ignore, results);
                }
                else
                {
                    var relPath = Path.GetRelativePath(root, entry);
                    if (pattern.IsMatch(relPath) || pattern.IsMatch(name))
                    {
                        results.Add(relPath);
                    }
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
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

        return new Regex(regexStr + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
