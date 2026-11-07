#nullable enable

using System.Text.Json;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Tools;

public class VerifyToolTests
{
    private static ToolExecutionContext Ctx() => new("test", Path.GetTempPath(), false, new Dictionary<string, string>(),
        new SessionInfo("test", "auto", new List<string>(), new List<string>()));

    private static JsonElement Input(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public async Task ReturnsTheRecordWithAttemptDefaultingToOne()
    {
        var result = await new VerifyTool().ExecuteAsync(Input(new { check = "pytest", passed = true, command_or_call_id = "call_9" }), Ctx());
        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal("pytest", doc.RootElement.GetProperty("check").GetString());
        Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("attempt").GetInt32());
        Assert.Equal("call_9", doc.RootElement.GetProperty("command_or_call_id").GetString());
    }

    [Fact]
    public async Task KeepsAttemptAndNote()
    {
        var result = await new VerifyTool().ExecuteAsync(Input(new { check = "dotnet build", passed = false, attempt = 2, note = "CS0103 in Foo.cs" }), Ctx());
        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.False(doc.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("attempt").GetInt32());
        Assert.Contains("CS0103", doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task RejectsMissingPassedOrCheck()
    {
        Assert.True((await new VerifyTool().ExecuteAsync(Input(new { check = "x" }), Ctx())).IsError);
        Assert.True((await new VerifyTool().ExecuteAsync(Input(new { check = "x", passed = "yes" }), Ctx())).IsError);
        Assert.True((await new VerifyTool().ExecuteAsync(Input(new { passed = true }), Ctx())).IsError);
        Assert.True((await new VerifyTool().ExecuteAsync(Input(new { check = "x", passed = true, attempt = 0 }), Ctx())).IsError);
    }
}
