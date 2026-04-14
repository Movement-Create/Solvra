#nullable enable

using System.Text.Json;
using ClosedXML.Excel;
using Solvra.Security;

namespace Solvra.Tools;

public class SpreadsheetCreateTool : ToolBase
{
    public override string Name => "spreadsheet_create";
    public override string Description => "Create Excel spreadsheet files.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Write;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            output_path = new { type = "string", description = "Output file path (.xlsx)" },
            sheets = new
            {
                type = "array",
                description = "Array of sheet definitions",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        headers = new { type = "array", items = new { type = "string" } },
                        rows = new { type = "array", items = new { type = "array" } }
                    }
                }
            }
        },
        required = new[] { "output_path", "sheets" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var outputPath = GetString(input, "output_path");
        if (!Path.IsPathRooted(outputPath))
            outputPath = Path.Combine(context.Cwd, outputPath);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (!input.TryGetProperty("sheets", out var sheetsElement) || sheetsElement.ValueKind != JsonValueKind.Array)
            return new ToolExecuteResult("Error: sheets array is required", true);

        try
        {
            using var workbook = new XLWorkbook();

            foreach (var sheetDef in sheetsElement.EnumerateArray())
            {
                var sheetName = sheetDef.TryGetProperty("name", out var n) ? n.GetString() ?? "Sheet" : "Sheet";
                var worksheet = workbook.Worksheets.Add(sheetName);

                // Write headers
                if (sheetDef.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
                {
                    var col = 1;
                    foreach (var header in headers.EnumerateArray())
                    {
                        worksheet.Cell(1, col).Value = header.GetString() ?? "";
                        worksheet.Cell(1, col).Style.Font.Bold = true;
                        col++;
                    }
                }

                // Write rows
                if (sheetDef.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    var row = 2;
                    foreach (var rowData in rows.EnumerateArray())
                    {
                        if (rowData.ValueKind != JsonValueKind.Array) continue;
                        var col = 1;
                        foreach (var cell in rowData.EnumerateArray())
                        {
                            worksheet.Cell(row, col).Value = cell.ValueKind switch
                            {
                                JsonValueKind.Number => cell.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => cell.GetString() ?? ""
                            };
                            col++;
                        }
                        row++;
                    }
                }

                worksheet.Columns().AdjustToContents();
            }

            await Task.Run(() => workbook.SaveAs(outputPath), ct);
            return new ToolExecuteResult($"Spreadsheet created: {outputPath}", false);
        }
        catch (Exception ex)
        {
            return new ToolExecuteResult($"Error creating spreadsheet: {ex.Message}", true);
        }
    }
}
