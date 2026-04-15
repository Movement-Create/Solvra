#nullable enable

using System.Text.Json;
using Solvra.Security;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Tools;

public class SpreadsheetCreateToolTests
{
    private static ToolExecutionContext MakeContext() => new(
        SessionId: "test",
        Cwd: Path.GetTempPath(),
        PlanMode: false,
        Env: new Dictionary<string, string>(),
        Session: new SessionInfo("test", "auto", new List<string>(), new List<string>()));

    [Fact]
    public async Task CreatesXlsxFile()
    {
        var tool = new SpreadsheetCreateTool();
        var outputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.xlsx");

        try
        {
            var input = JsonSerializer.SerializeToElement(new
            {
                output_path = outputPath,
                sheets = new[]
                {
                    new
                    {
                        name = "Data",
                        headers = new[] { "Name", "Value" },
                        rows = new[]
                        {
                            new object[] { "foo", 1 },
                            new object[] { "bar", 2 }
                        }
                    }
                }
            });

            var result = await tool.ExecuteAsync(input, MakeContext());
            Assert.False(result.IsError);
            Assert.Contains("Spreadsheet created", result.Output);
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task CreatesMultiSheetWorkbook()
    {
        var tool = new SpreadsheetCreateTool();
        var outputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.xlsx");

        try
        {
            var input = JsonSerializer.SerializeToElement(new
            {
                output_path = outputPath,
                sheets = new[]
                {
                    new
                    {
                        name = "Sheet1",
                        headers = new[] { "A" },
                        rows = new[] { new object[] { "val1" } }
                    },
                    new
                    {
                        name = "Sheet2",
                        headers = new[] { "B" },
                        rows = new[] { new object[] { "val2" } }
                    }
                }
            });

            var result = await tool.ExecuteAsync(input, MakeContext());
            Assert.False(result.IsError);
            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RejectsNoSheets()
    {
        var tool = new SpreadsheetCreateTool();
        var input = JsonSerializer.SerializeToElement(new
        {
            output_path = "/tmp/test.xlsx"
            // Missing sheets
        });

        var result = await tool.ExecuteAsync(input, MakeContext());
        Assert.True(result.IsError);
        Assert.Contains("sheets", result.Output);
    }

    [Fact]
    public void ToolMetadata()
    {
        var tool = new SpreadsheetCreateTool();
        Assert.Equal("spreadsheet_create", tool.Name);
        Assert.Equal(PermissionLevel.Write, tool.PermissionLevel);
    }
}
