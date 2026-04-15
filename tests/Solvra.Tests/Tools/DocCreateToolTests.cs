#nullable enable

using System.Text.Json;
using Solvra.Security;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Tools;

public class DocCreateToolTests
{
    private static ToolExecutionContext MakeContext() => new(
        SessionId: "test",
        Cwd: Path.GetTempPath(),
        PlanMode: false,
        Env: new Dictionary<string, string>(),
        Session: new SessionInfo("test", "auto", new List<string>(), new List<string>()));

    [Fact]
    public async Task CreatesPdfFile()
    {
        var tool = new DocCreateTool();
        var outputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.pdf");

        try
        {
            var input = JsonSerializer.SerializeToElement(new
            {
                format = "pdf",
                title = "Test Document",
                content = "This is a test PDF document with some content.",
                output_path = outputPath
            });

            var result = await tool.ExecuteAsync(input, MakeContext());
            Assert.False(result.IsError);
            Assert.Contains("PDF created", result.Output);
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task CreatesCsvFile()
    {
        var tool = new DocCreateTool();
        var outputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.csv");

        try
        {
            var input = JsonSerializer.SerializeToElement(new
            {
                format = "csv",
                title = "Test CSV",
                content = "name,value\nfoo,1\nbar,2",
                output_path = outputPath
            });

            var result = await tool.ExecuteAsync(input, MakeContext());
            Assert.False(result.IsError);
            Assert.Contains("CSV created", result.Output);
            Assert.True(File.Exists(outputPath));

            var content = await File.ReadAllTextAsync(outputPath);
            Assert.Contains("Test CSV", content);
            Assert.Contains("foo,1", content);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RejectsUnsupportedFormat()
    {
        var tool = new DocCreateTool();
        var input = JsonSerializer.SerializeToElement(new
        {
            format = "docx",
            title = "Test",
            content = "Test content",
            output_path = "/tmp/test.docx"
        });

        var result = await tool.ExecuteAsync(input, MakeContext());
        Assert.True(result.IsError);
        Assert.Contains("Unsupported format", result.Output);
    }

    [Fact]
    public void ToolMetadata()
    {
        var tool = new DocCreateTool();
        Assert.Equal("doc_create", tool.Name);
        Assert.Equal(PermissionLevel.Write, tool.PermissionLevel);
    }
}
