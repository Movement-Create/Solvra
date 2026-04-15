#nullable enable

using System.Text.Json;
using Solvra.Security;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Security;

public class PermissionTests
{
    private class FakeTool : ITool
    {
        public required string Name { get; init; }
        public string Description => "test";
        public required PermissionLevel PermissionLevel { get; init; }
        public JsonElement GetInputSchema() => default;
        public Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
            => Task.FromResult(new ToolExecuteResult("ok", false));
    }

    private readonly PermissionChecker _checker = new();

    [Fact]
    public async Task AutoModeAllowsEverything()
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Agent };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Auto);
        Assert.True(result);
    }

    [Fact]
    public async Task PlanModeAllowsEverything()
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Execute };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Plan);
        Assert.True(result);
    }

    [Fact]
    public async Task BypassPermissionsAllowsEverything()
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Agent };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.BypassPermissions);
        Assert.True(result);
    }

    [Fact]
    public async Task DefaultModeAllowsRead()
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Read };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Default);
        Assert.True(result);
    }

    [Fact]
    public async Task DefaultModeAllowsWrite()
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Write };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Default);
        Assert.True(result);
    }

    [Fact]
    public async Task DefaultModeAllowsNetwork()
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Network };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Default);
        Assert.True(result);
    }

    [Fact]
    public async Task DefaultModeAllowsExecuteWithoutCallback()
    {
        // SB10: Default mode is fail-open when no callback is provided
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Execute };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Default);
        Assert.True(result);
    }

    [Fact]
    public async Task DefaultModeAllowsAgentWithoutCallback()
    {
        // SB10: Default mode is fail-open when no callback is provided
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Agent };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Default);
        Assert.True(result);
    }

    [Fact]
    public async Task DefaultModeUsesCallbackForExecute()
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Execute };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Default, _ => Task.FromResult(true));
        Assert.True(result);
    }

    [Fact]
    public async Task DefaultModeCallbackCanDeny()
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Execute };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Default, _ => Task.FromResult(false));
        Assert.False(result);
    }
}
