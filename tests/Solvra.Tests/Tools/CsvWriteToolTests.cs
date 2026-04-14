#nullable enable

using System.Text.Json;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Tools;

public class CsvWriteToolTests
{
    private static ToolExecutionContext MakeContext() => new(
        SessionId: "test",
        Cwd: Path.GetTempPath(),
        PlanMode: false,
        Env: new Dictionary<string, string>(),
        Session: new SessionInfo("test", "auto", new List<string>(), new List<string>()));

    private static JsonElement MakeInput(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    [Fact]
    public async Task WritesHeadersAndRows()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.csv");
        try
        {
            var tool = new CsvWriteTool();
            var result = await tool.ExecuteAsync(MakeInput(new
            {
                output_path = outputPath,
                headers = new[] { "Name", "Age", "City" },
                rows = new[] {
                    new object[] { "Alice", 30, "NYC" },
                    new object[] { "Bob", 25, "LA" }
                }
            }), MakeContext());

            Assert.False(result.IsError);
            Assert.True(File.Exists(outputPath));

            var content = await File.ReadAllTextAsync(outputPath);
            Assert.Contains("Name,Age,City", content);
            Assert.Contains("Alice,30,NYC", content);
            Assert.Contains("Bob,25,LA", content);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void EscapesCsvFieldsCorrectly()
    {
        Assert.Equal("simple", CsvWriteTool.EscapeCsvField("simple"));
        Assert.Equal("\"has,comma\"", CsvWriteTool.EscapeCsvField("has,comma"));
        Assert.Equal("\"has\"\"quote\"", CsvWriteTool.EscapeCsvField("has\"quote"));
        Assert.Equal("\"has\nnewline\"", CsvWriteTool.EscapeCsvField("has\nnewline"));
    }
}
