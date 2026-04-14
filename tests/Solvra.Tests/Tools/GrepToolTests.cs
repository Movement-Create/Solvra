#nullable enable

using System.Text.Json;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Tools;

public class GrepToolTests
{
    private static ToolExecutionContext MakeContext(string cwd) => new(
        SessionId: "test",
        Cwd: cwd,
        PlanMode: false,
        Env: new Dictionary<string, string>(),
        Session: new SessionInfo("test", "auto", new List<string>(), new List<string>()));

    private static JsonElement MakeInput(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    [Fact]
    public async Task FindsPatternInFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"grep_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "test.txt"), "hello world\nfoo bar\nhello again");

            var tool = new GrepTool();
            var result = await tool.ExecuteAsync(
                MakeInput(new { pattern = "hello", path = tempDir }),
                MakeContext(tempDir));

            Assert.False(result.IsError);
            Assert.Contains("hello world", result.Output);
            Assert.Contains("hello again", result.Output);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RegexPatternMatching()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"grep_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "code.cs"),
                "public class Foo {}\npublic class Bar {}\nprivate int x;");

            var tool = new GrepTool();
            var result = await tool.ExecuteAsync(
                MakeInput(new { pattern = @"public class \w+", path = tempDir }),
                MakeContext(tempDir));

            Assert.False(result.IsError);
            Assert.Contains("public class Foo", result.Output);
            Assert.Contains("public class Bar", result.Output);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InvalidRegexReturnsError()
    {
        var tool = new GrepTool();
        var result = await tool.ExecuteAsync(
            MakeInput(new { pattern = "[invalid" }),
            MakeContext(Path.GetTempPath()));

        Assert.True(result.IsError);
        Assert.Contains("invalid regex", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GlobFilterWorks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"grep_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "file.cs"), "hello in cs");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "file.txt"), "hello in txt");

            var tool = new GrepTool();
            var result = await tool.ExecuteAsync(
                MakeInput(new { pattern = "hello", path = tempDir, glob = "*.cs" }),
                MakeContext(tempDir));

            Assert.False(result.IsError);
            Assert.Contains("hello in cs", result.Output);
            Assert.DoesNotContain("hello in txt", result.Output);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
