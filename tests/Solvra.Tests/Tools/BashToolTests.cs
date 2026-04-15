#nullable enable

using System.Text.Json;
using Solvra.Security;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Tools;

public class BashToolTests
{
    private static ToolExecutionContext MakeContext(string? cwd = null) => new(
        SessionId: "test",
        Cwd: cwd ?? Path.GetTempPath(),
        PlanMode: false,
        Env: new Dictionary<string, string>(),
        Session: new SessionInfo("test", "auto", new List<string>(), new List<string>()));

    private static JsonElement MakeInput(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    [Fact]
    public async Task ExecuteSimpleCommand()
    {
        var tool = new BashTool(new SandboxManager());
        var result = await tool.ExecuteAsync(MakeInput(new { command = "echo hello" }), MakeContext());

        Assert.False(result.IsError);
        Assert.Contains("hello", result.Output);
    }

    [Fact]
    public async Task CommandTimeout()
    {
        var tool = new BashTool(new SandboxManager());
        var result = await tool.ExecuteAsync(
            MakeInput(new { command = "sleep 30", timeout_ms = 1000 }),
            MakeContext());

        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlocksDangerousCommand()
    {
        var tool = new BashTool(new SandboxManager());
        var result = await tool.ExecuteAsync(
            MakeInput(new { command = "rm -rf /" }),
            MakeContext());

        Assert.True(result.IsError);
        Assert.Contains("Blocked", result.Output);
    }

    [Fact]
    public async Task OutputTruncation()
    {
        var tool = new BashTool(new SandboxManager());
        // Generate a large amount of output
        var result = await tool.ExecuteAsync(
            MakeInput(new { command = "yes 'x' | head -60000" }),
            MakeContext());

        // Output should be present (sandbox caps at 1MB, tool may also truncate)
        Assert.True(result.Output.Length > 0, "Output should not be empty");
        Assert.True(result.Output.Length <= 1_100_000, $"Output length was {result.Output.Length}");
    }

    [Fact]
    public async Task NonZeroExitCodeIsError()
    {
        var tool = new BashTool(new SandboxManager());
        var result = await tool.ExecuteAsync(
            MakeInput(new { command = "exit 1" }),
            MakeContext());

        Assert.True(result.IsError);
    }
}
