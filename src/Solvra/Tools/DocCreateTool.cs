#nullable enable

using System.Text.Json;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Solvra.Security;

namespace Solvra.Tools;

public class DocCreateTool : ToolBase
{
    public override string Name => "doc_create";
    public override string Description => "Create PDF or CSV documents.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Write;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            format = new { type = "string", description = "Output format: pdf or csv" },
            title = new { type = "string", description = "Document title" },
            content = new { type = "string", description = "Document content" },
            output_path = new { type = "string", description = "Output file path" }
        },
        required = new[] { "format", "title", "content", "output_path" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var format = GetString(input, "format").ToLowerInvariant();
        var title = GetString(input, "title");
        var content = GetString(input, "content");
        var outputPath = GetString(input, "output_path");

        if (!Path.IsPathRooted(outputPath))
            outputPath = Path.Combine(context.Cwd, outputPath);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return format switch
        {
            "pdf" => await CreatePdfAsync(title, content, outputPath, ct),
            "csv" => await CreateCsvDocAsync(title, content, outputPath, ct),
            _ => new ToolExecuteResult($"Unsupported format: {format}. Use 'pdf' or 'csv'.", true)
        };
    }

    private static Task<ToolExecuteResult> CreatePdfAsync(string title, string content, string outputPath, CancellationToken ct)
    {
        try
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            var document = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(50);
                    page.Header().Text(title).FontSize(20).Bold();
                    page.Content().PaddingVertical(10).Text(content).FontSize(12);
                    page.Footer().AlignCenter().Text(x =>
                    {
                        x.Span("Page ");
                        x.CurrentPageNumber();
                    });
                });
            });

            document.GeneratePdf(outputPath);

            return Task.FromResult(new ToolExecuteResult($"PDF created: {outputPath}", false));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolExecuteResult($"Error creating PDF: {ex.Message}", true));
        }
    }

    private static async Task<ToolExecuteResult> CreateCsvDocAsync(string title, string content, string outputPath, CancellationToken ct)
    {
        // For CSV format, write content directly as CSV with title as comment
        var csvContent = $"# {title}\n{content}";
        await File.WriteAllTextAsync(outputPath, csvContent, ct);
        return new ToolExecuteResult($"CSV created: {outputPath}", false);
    }
}
