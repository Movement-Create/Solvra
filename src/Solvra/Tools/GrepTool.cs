#nullable enable

using System.Text.Json;
using System.Text.RegularExpressions;
using Solvra.Security;

namespace Solvra.Tools;

public class GrepTool : ToolBase
{
    public override string Name => "grep";
    public override string Description => "Search file contents using regex patterns.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Read;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            pattern = new { type = "string", description = "Regex pattern to search for" },
            glob = new { type = "string", description = "File glob filter (e.g., *.cs)" },
            path = new { type = "string", description = "Base directory to search" }
        },
        required = new[] { "pattern" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var pattern = GetString(input, "pattern");
        var globFilter = GetOptionalString(input, "glob");
        var basePath = GetOptionalString(input, "path") ?? context.Cwd;

        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(context.Cwd, basePath);

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            return new ToolExecuteResult($"Error: invalid regex: {ex.Message}", true);
        }

        Regex? globRegex = null;
        if (!string.IsNullOrEmpty(globFilter))
            globRegex = GlobTool.GlobToRegex(globFilter);

        var ignoreSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", ".vs", "__pycache__"
        };

        var results = new List<string>();
        var maxResults = 250;

        await Task.Run(() => SearchDirectory(basePath, basePath, regex, globRegex, ignoreSet, results, maxResults), ct);

        if (results.Count == 0)
            return new ToolExecuteResult("No matches found.", false);

        return new ToolExecuteResult(string.Join('\n', results), false);
    }

    private static void SearchDirectory(
        string root, string current, Regex pattern, Regex? globFilter,
        HashSet<string> ignore, List<string> results, int maxResults)
    {
        if (results.Count >= maxResults) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (results.Count >= maxResults) return;

                var name = Path.GetFileName(file);
                var relPath = Path.GetRelativePath(root, file);

                if (globFilter != null && !globFilter.IsMatch(relPath) && !globFilter.IsMatch(name))
                    continue;

                // Skip binary files
                if (IsBinaryFile(file)) continue;

                try
                {
                    var lines = File.ReadLines(file);
                    var lineNum = 0;
                    foreach (var line in lines)
                    {
                        lineNum++;
                        if (results.Count >= maxResults) return;

                        if (pattern.IsMatch(line))
                        {
                            results.Add($"{relPath}:{lineNum}: {line.TrimEnd()}");
                        }
                    }
                }
                catch (Exception) { }
            }

            foreach (var dir in Directory.EnumerateDirectories(current))
            {
                var dirName = Path.GetFileName(dir);
                if (ignore.Contains(dirName)) continue;
                SearchDirectory(root, dir, pattern, globFilter, ignore, results, maxResults);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static bool IsBinaryFile(string path)
    {
        try
        {
            var buffer = new byte[512];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytesRead = fs.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0) return true;
            }
            return false;
        }
        catch { return true; }
    }
}
