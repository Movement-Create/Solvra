#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class FileEditTool : ToolBase
{
    public override string Name => "file_edit";
    public override string Description => "Search and replace text in a file.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Write;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "File path to edit" },
            old_string = new { type = "string", description = "Text to find" },
            new_string = new { type = "string", description = "Replacement text" }
        },
        required = new[] { "path", "old_string", "new_string" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var path = GetString(input, "path");
        var oldString = GetString(input, "old_string");
        var newString = GetString(input, "new_string");

        if (!Path.IsPathRooted(path))
            path = Path.Combine(context.Cwd, path);

        if (!File.Exists(path))
            return new ToolExecuteResult($"Error: file not found: {path}", true);

        var content = await File.ReadAllTextAsync(path, ct);

        var occurrences = CountOccurrences(content, oldString);
        if (occurrences == 0)
            return new ToolExecuteResult("Error: old_string not found in file", true);
        if (occurrences > 1)
            return new ToolExecuteResult($"Error: old_string found {occurrences} times. Provide more context to make it unique.", true);

        var newContent = content.Replace(oldString, newString);
        await File.WriteAllTextAsync(path, newContent, ct);

        return new ToolExecuteResult($"File edited: {path}", false);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
