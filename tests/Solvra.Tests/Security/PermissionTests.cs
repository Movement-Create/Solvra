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

    [Theory]
    [InlineData(PermissionLevel.Read, true)]
    [InlineData(PermissionLevel.Network, true)]
    [InlineData(PermissionLevel.Write, false)]
    [InlineData(PermissionLevel.Execute, false)]
    [InlineData(PermissionLevel.Agent, false)]
    public async Task PlanModeIsReadOnly(PermissionLevel level, bool expected)
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = level };
        // Even an approving callback must not unlock writes in plan mode.
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Plan, _ => Task.FromResult(true));
        Assert.Equal(expected, result);
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
    public async Task DefaultModeAsksForWrite()
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Write };
        Assert.False(await _checker.CheckPermissionAsync(tool, PermissionMode.Default, _ => Task.FromResult(false)));
        Assert.True(await _checker.CheckPermissionAsync(tool, PermissionMode.Default, _ => Task.FromResult(true)));
    }

    [Fact]
    public async Task DefaultModeAllowsNetwork()
    {
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Network };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Default);
        Assert.True(result);
    }

    [Fact]
    public async Task DefaultModeDeniesExecuteWithoutCallback()
    {
        // Fail closed: with nobody to ask, Execute is refused.
        var tool = new FakeTool { Name = "test", PermissionLevel = PermissionLevel.Execute };
        var result = await _checker.CheckPermissionAsync(tool, PermissionMode.Default);
        Assert.False(result);
    }

    [Fact]
    public async Task DefaultModeDeniesWriteAndAgentWithoutCallback()
    {
        foreach (var level in new[] { PermissionLevel.Write, PermissionLevel.Agent })
        {
            var tool = new FakeTool { Name = "test", PermissionLevel = level };
            Assert.False(await _checker.CheckPermissionAsync(tool, PermissionMode.Default));
        }
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
