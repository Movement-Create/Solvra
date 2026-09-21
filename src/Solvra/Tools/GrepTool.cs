#nullable enable

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Solvra.Security;

namespace Solvra.Tools;

public class GrepTool : ToolBase
{
    private const int DefaultMaxResults = 200;
    private const int MaxFileBytes = 5 * 1024 * 1024;

    public override string Name => "grep";

    public override string Description =>
        "Search file contents with a .NET regular expression. Skips .git, node_modules, bin/obj, binaries and " +
        ".gitignore'd paths. output_mode: \"content\" (default, file:line: text), \"files\" (matching paths only) " +
        "or \"count\". Use glob to filter files (e.g. \"*.cs\", \"src/**/*.ts\"), context for surrounding lines, " +
        "case_insensitive for case-insensitive matching. path may be a directory or a single file.";

    public override PermissionLevel PermissionLevel => PermissionLevel.Read;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            pattern = new { type = "string", description = "Regex pattern to search for" },
            path = new { type = "string", description = "Directory or file to search (default: working directory)" },
            glob = new { type = "string", description = "File glob filter (e.g. *.cs or src/**/*.py)" },
            output_mode = new { type = "string", @enum = new[] { "content", "files", "count" }, description = "content (default), files or count" },
            case_insensitive = new { type = "boolean", description = "Case-insensitive match" },
            context = new { type = "integer", description = "Lines of context before and after each match (content mode)" },
            max_results = new { type = "integer", description = $"Maximum matches/files to return (default {DefaultMaxResults})" }
        },
        required = new[] { "pattern" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var pattern = GetString(input, "pattern");
        if (string.IsNullOrEmpty(pattern))
            return new ToolExecuteResult("Error: pattern is required", true);

        var globFilter = GetOptionalString(input, "glob");
        var basePath = GetOptionalString(input, "path") is { Length: > 0 } p ? p : context.Cwd;
        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(context.Cwd, basePath);

        var mode = (GetOptionalString(input, "output_mode") ?? "content").ToLowerInvariant();
        if (mode is not ("content" or "files" or "count"))
            return new ToolExecuteResult("Error: output_mode must be content, files or count", true);
        var caseInsensitive = input.TryGetProperty("case_insensitive", out var ci) && ci.ValueKind == JsonValueKind.True;
        var contextLines = Math.Clamp(GetOptionalInt(input, "context") ?? 0, 0, 10);
        var maxResults = Math.Clamp(GetOptionalInt(input, "max_results") ?? DefaultMaxResults, 1, 2000);

        Regex regex;
        try
        {
            var opts = RegexOptions.Compiled | (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(pattern, opts, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return new ToolExecuteResult($"Error: invalid regex: {ex.Message}", true);
        }

        Regex? globRegex = string.IsNullOrEmpty(globFilter) ? null : GlobTool.GlobToRegex(globFilter);

        IEnumerable<(string FullPath, string RelPath)> files;
        if (File.Exists(basePath))
            files = [(basePath, Path.GetFileName(basePath))];
        else if (Directory.Exists(basePath))
            files = new FileWalker(basePath).Files(ct: ct);
        else
            return new ToolExecuteResult($"Error: path not found: {basePath}", true);

        var output = new StringBuilder();
        var results = 0;
        var filesMatched = 0;
        var truncated = false;

        await Task.Run(() =>
        {
            foreach (var (full, rel) in files)
            {
                if (results >= maxResults) { truncated = true; break; }
                if (globRegex != null && !GlobTool.Matches(globRegex, globFilter!, rel)) continue;
                try { if (new FileInfo(full).Length > MaxFileBytes) continue; } catch { continue; }
                if (FileWalker.IsBinaryFile(full)) continue;

                string[] lines;
                try { lines = File.ReadAllLines(full); } catch { continue; }

                var matchCount = 0;
                var lastPrinted = -1;
                for (var i = 0; i < lines.Length; i++)
                {
                    bool isMatch;
                    try { isMatch = regex.IsMatch(lines[i]); } catch (RegexMatchTimeoutException) { isMatch = false; }
                    if (!isMatch) continue;
                    matchCount++;
                    if (mode != "content") continue;
                    if (results >= maxResults) { truncated = true; break; }
                    results++;

                    var from = Math.Max(0, i - contextLines);
                    var to = Math.Min(lines.Length - 1, i + contextLines);
                    if (contextLines > 0 && lastPrinted >= 0 && from > lastPrinted + 1) output.AppendLine("--");
                    for (var j = Math.Max(from, lastPrinted + 1); j <= to; j++)
                    {
                        var sep = j == i ? ":" : "-";
                        output.Append(rel).Append(sep).Append(j + 1).Append(sep).Append(' ')
                              .AppendLine(ToolOutput.ClipLongLines(lines[j].TrimEnd()));
                    }
                    lastPrinted = to;
                }

                if (matchCount == 0) continue;
                filesMatched++;
                if (mode == "files") { output.AppendLine(rel); results++; }
                else if (mode == "count") { output.Append(rel).Append(':').Append(matchCount).AppendLine(); results++; }
            }
        }, ct);

        if (output.Length == 0)
            return new ToolExecuteResult($"No matches for /{pattern}/ in {basePath}" + (globFilter != null ? $" (glob {globFilter})" : "") + ".", false);

        var text = output.ToString().TrimEnd();
        if (truncated)
            text += $"\n[Stopped after {maxResults} results; narrow the pattern/glob or raise max_results.]";
        return new ToolExecuteResult(ToolOutput.Limit(text, "grep"), false);
    }
}
