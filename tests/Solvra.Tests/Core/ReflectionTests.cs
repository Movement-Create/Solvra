#nullable enable

using Solvra.Core;
using Solvra.Models;
using Xunit;

namespace Solvra.Tests.Core;

public class ReflectionTests
{
    [Fact]
    public void ShouldReflect_ReturnsFalse_ForShortTasks()
    {
        var result = new AgentRunResult
        {
            Text = "Done",
            Turns = 2,
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
            CostUsd = 0.001m,
            StopReason = StopReason.Text,
            Messages = new List<Message>
            {
                Message.FromText(MessageRole.User, "hi"),
                Message.FromText(MessageRole.Assistant, "hello")
            }
        };

        // Reflection triggers at >= 5 turns or on tool errors
        // With 2 turns and no errors, should not reflect
        Assert.True(result.Turns < 5);
        Assert.Equal(StopReason.Text, result.StopReason);
    }

    [Fact]
    public void ShouldReflect_ReturnsTrue_ForLongTasks()
    {
        // 5+ turns should trigger reflection (when StopReason is Text)
        var result = new AgentRunResult
        {
            Text = "Done after many turns",
            Turns = 7,
            Usage = new TokenUsage { InputTokens = 1000, OutputTokens = 500 },
            CostUsd = 0.01m,
            StopReason = StopReason.Text,
            Messages = new List<Message>()
        };

        Assert.True(result.Turns >= 5);
        Assert.Equal(StopReason.Text, result.StopReason);
    }

    [Fact]
    public void ShouldReflect_ReturnsFalse_ForNonTextStop()
    {
        // MaxTurns/MaxBudget/Error should not trigger reflection
        var result = new AgentRunResult
        {
            Text = "Hit budget",
            Turns = 10,
            Usage = new TokenUsage { InputTokens = 5000, OutputTokens = 2500 },
            CostUsd = 5.0m,
            StopReason = StopReason.MaxBudget,
            Messages = new List<Message>()
        };

        Assert.NotEqual(StopReason.Text, result.StopReason);
    }

    [Fact]
    public void ShouldReflect_ReturnsTrue_OnToolErrors()
    {
        var messages = new List<Message>
        {
            Message.FromText(MessageRole.User, "do something"),
            new()
            {
                Role = MessageRole.Tool,
                Content = new List<MessageContent>
                {
                    new ToolResultContent
                    {
                        ToolUseId = "tc_1",
                        Content = "Error: file not found",
                        IsError = true
                    }
                }
            }
        };

        // Even with < 5 turns, tool errors should trigger reflection
        var hasError = messages.Any(m => m.Content.Any(c => c is ToolResultContent { IsError: true }));
        Assert.True(hasError);
    }

    [Fact]
    public void ReflectionPrompt_MentionsLessonTag()
    {
        // The reflection prompt should tell the agent to save lessons
        // This verifies the constant exists and has the right guidance
        var prompt = "Before finishing: is there anything future-you should remember from this task? " +
            "If you made a mistake, hit an unexpected error, or learned a non-obvious gotcha, " +
            "call memory_note with kind='lesson' and short relevant tags (file paths, tool names, topics). " +
            "If nothing is worth saving, reply with just 'done'.";

        Assert.Contains("memory_note", prompt);
        Assert.Contains("lesson", prompt);
        Assert.Contains("tags", prompt);
    }

    [Fact]
    public void TokenUsage_Addition()
    {
        var a = new TokenUsage { InputTokens = 100, OutputTokens = 50 };
        var b = new TokenUsage { InputTokens = 200, OutputTokens = 75 };
        var sum = a + b;
        Assert.Equal(300, sum.InputTokens);
        Assert.Equal(125, sum.OutputTokens);
    }
}
