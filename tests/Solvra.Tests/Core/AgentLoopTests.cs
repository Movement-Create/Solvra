#nullable enable

using System.Text.Json;
using Solvra.Core;
using Solvra.Models;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Core;

public class AgentLoopTests
{
    private static SessionConfig MakeSession(int maxTurns = 10) => new()
    {
        Id = "test",
        CreatedAt = DateTime.UtcNow.ToString("o"),
        Model = "test-model",
        Provider = "test",
        MaxTurns = maxTurns,
        MaxBudgetUsd = 10m
    };

    [Fact]
    public void ToolRegistry_IsInitiallyEmpty()
    {
        var registry = new ToolRegistry();
        Assert.Empty(registry.GetToolDefinitions());
        Assert.Null(registry.GetTool("nonexistent"));
    }

    [Fact]
    public void MaxTurns_DefaultIs50InSessionConfig()
    {
        var config = new SessionConfig
        {
            Id = "test",
            CreatedAt = DateTime.UtcNow.ToString("o")
        };
        Assert.Equal(50, config.MaxTurns);
    }

    [Fact]
    public void AgentRunOptions_SubagentDepthDefault()
    {
        var options = new AgentRunOptions
        {
            Prompt = "test",
            Session = MakeSession()
        };
        Assert.Equal(0, options.SubagentDepth);
    }

    [Fact]
    public void StopReason_EnumValues()
    {
        Assert.Equal(0, (int)StopReason.Text);
        Assert.Equal(1, (int)StopReason.MaxTurns);
        Assert.Equal(2, (int)StopReason.MaxBudget);
        Assert.Equal(3, (int)StopReason.Error);
    }

    [Fact]
    public void AgentRunResult_MessagesAreCaptured()
    {
        var messages = new List<Message>
        {
            Message.FromText(MessageRole.User, "hi"),
            Message.FromText(MessageRole.Assistant, "hello")
        };

        var result = new AgentRunResult
        {
            Text = "hello",
            Turns = 1,
            Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 },
            CostUsd = 0.001m,
            StopReason = StopReason.Text,
            Messages = messages
        };

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("hello", result.Text);
        Assert.Equal(1, result.Turns);
    }

    [Fact]
    public async Task ToolRegistry_ExecuteReturnsErrorForUnknownTool()
    {
        var registry = new ToolRegistry();
        var input = JsonDocument.Parse("{}").RootElement;
        var context = new ToolExecutionContext(
            SessionId: "test",
            Cwd: Directory.GetCurrentDirectory(),
            PlanMode: false,
            Env: new Dictionary<string, string>(),
            Session: new Solvra.Tools.SessionInfo("test", "auto", new List<string>(), new List<string>()));
        var result = await registry.ExecuteToolAsync("nonexistent", input, context);
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolRegistry_RegisterAndRetrieve()
    {
        var registry = new ToolRegistry();
        var tool = new StubTool("test_tool", "A test tool");
        registry.RegisterTool(tool);

        Assert.NotNull(registry.GetTool("test_tool"));
        var defs = registry.GetToolDefinitions();
        Assert.Single(defs);
        Assert.Equal("test_tool", defs[0].Name);
    }

    [Fact]
    public void ToolRegistry_GetAllTools()
    {
        var registry = new ToolRegistry();
        registry.RegisterTool(new StubTool("a", "Tool A"));
        registry.RegisterTool(new StubTool("b", "Tool B"));
        Assert.Equal(2, registry.GetAllTools().Count);
    }

    /// <summary>Minimal ITool stub for testing.</summary>
    private class StubTool : ITool
    {
        public string Name { get; }
        public string Description { get; }
        public Solvra.Security.PermissionLevel PermissionLevel => Solvra.Security.PermissionLevel.Read;

        public StubTool(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public JsonElement GetInputSchema() =>
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement;

        public Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default) =>
            Task.FromResult(new ToolExecuteResult("stub output", false));
    }
}
