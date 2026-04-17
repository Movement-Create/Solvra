#nullable enable

using System.Text;
using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class CsvWriteTool : ToolBase
{
    public override string Name => "csv_write";
    public override string Description => "Write data to a CSV file with proper escaping.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Write;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            output_path = new { type = "string", description = "Output file path" },
            headers = new { type = "array", items = new { type = "string" }, description = "Column headers" },
            rows = new { type = "array", items = new { type = "array", items = new { type = "string" } }, description = "Row data (array of arrays)" }
        },
        required = new[] { "output_path", "headers", "rows" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var outputPath = GetString(input, "output_path");
        if (!Path.IsPathRooted(outputPath))
            outputPath = Path.Combine(context.Cwd, outputPath);

        var headers = GetStringArray(input, "headers");
        if (headers.Length == 0)
            return new ToolExecuteResult("Error: headers are required", true);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();

        // Write headers
        sb.AppendLine(string.Join(",", headers.Select(EscapeCsvField)));

        // Write rows
        if (input.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array) continue;

                var fields = row.EnumerateArray()
                    .Select(cell => cell.ValueKind switch
                    {
                        JsonValueKind.String => cell.GetString() ?? "",
                        JsonValueKind.Number => cell.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => "",
                        _ => cell.GetRawText()
                    })
                    .Select(EscapeCsvField);

                sb.AppendLine(string.Join(",", fields));
            }
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), ct);

        return new ToolExecuteResult($"CSV written: {outputPath}", false);
    }

    public static string EscapeCsvField(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }
}
