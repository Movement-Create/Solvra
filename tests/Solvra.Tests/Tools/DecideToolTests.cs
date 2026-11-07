#nullable enable

using System.Text.Json;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Tools;

public class DecideToolTests
{
    private static ToolExecutionContext Ctx() => new("test", Path.GetTempPath(), false, new Dictionary<string, string>(),
        new SessionInfo("test", "auto", new List<string>(), new List<string>()));

    private static JsonElement Input(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public async Task ReturnsTheRecordUnchanged()
    {
        var result = await new DecideTool().ExecuteAsync(Input(new
        {
            question = "Divide by scalar dx or by np.diff(x)?",
            options = new[] { "scalar dx", "np.diff(x)" },
            choice = "np.diff(x)",
            confidence = 0.9,
            evidence = new[] { "call_1", "call_2" },
            rationale = "spacing may be non-uniform"
        }), Ctx());

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Output);
        var r = doc.RootElement;
        Assert.Equal("np.diff(x)", r.GetProperty("choice").GetString());
        Assert.Equal(0.9, r.GetProperty("confidence").GetDouble());
        Assert.Equal(2, r.GetProperty("evidence").GetArrayLength());
        Assert.Equal("spacing may be non-uniform", r.GetProperty("rationale").GetString());
    }

    [Fact]
    public async Task OmitsAbsentOptionalFields()
    {
        var result = await new DecideTool().ExecuteAsync(Input(new { question = "q", choice = "a" }), Ctx());
        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.False(doc.RootElement.TryGetProperty("confidence", out _));
        Assert.False(doc.RootElement.TryGetProperty("options", out _));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public async Task RejectsConfidenceOutsideZeroToOne(double confidence)
    {
        var result = await new DecideTool().ExecuteAsync(Input(new { question = "q", choice = "a", confidence }), Ctx());
        Assert.True(result.IsError);
        Assert.Contains("confidence", result.Output);
    }

    [Fact]
    public async Task RequiresQuestionAndChoice()
    {
        Assert.True((await new DecideTool().ExecuteAsync(Input(new { choice = "a" }), Ctx())).IsError);
        Assert.True((await new DecideTool().ExecuteAsync(Input(new { question = "q", choice = " " }), Ctx())).IsError);
    }

    [Fact]
    public void IsRegisteredAsReadLevelBuiltin()
    {
        var registry = new ToolRegistry();
        registry.RegisterBuiltins(new Solvra.Security.SandboxManager());
        var tool = registry.GetTool("decide");
        Assert.NotNull(tool);
        Assert.Equal(Solvra.Security.PermissionLevel.Read, tool!.PermissionLevel);
        Assert.NotNull(registry.GetTool("verify"));
    }
}
