using Xunit;
using Solvra.Core;
using Solvra.Models;

namespace Solvra.Tests.Core;

public class ContextTests
{
    [Fact]
    public void EstimateTokens_ReturnsCorrectApproximation()
    {
        Assert.Equal(3, Context.EstimateTokens("Hello World!")); // 12 chars / 4 = 3
        Assert.Equal(0, Context.EstimateTokens(""));
        Assert.Equal(1, Context.EstimateTokens("abc")); // ceil(3/4) = 1
        Assert.Equal(25, Context.EstimateTokens(new string('a', 100))); // 100/4 = 25
    }

    [Fact]
    public void EstimateContextTokens_SumsMessageTokens()
    {
        var messages = new List<Message>
        {
            Message.FromText(MessageRole.User, new string('a', 40)),     // 10 tokens
            Message.FromText(MessageRole.Assistant, new string('b', 80)) // 20 tokens
        };

        var tokens = Context.EstimateContextTokens(messages);
        Assert.Equal(30, tokens);
    }

    [Theory]
    [InlineData("claude-3-5-sonnet-20241022", 200_000)]
    [InlineData("gpt-4o", 128_000)]
    [InlineData("gemini-2.5-pro", 1_000_000)]
    [InlineData("llama3.1", 128_000)]
    [InlineData("unknown-model-xyz", 128_000)]
    public void GetContextLimit_ReturnsCorrectLimits(string model, int expected)
    {
        Assert.Equal(expected, Context.GetContextLimit(model));
    }

    [Fact]
    public void GetContextLimit_PrefixMatch()
    {
        // "claude-42-turbo" should match the "claude-3" prefix group
        var limit = Context.GetContextLimit("claude-3-new-variant");
        Assert.Equal(200_000, limit);
    }

    [Fact]
    public void AssembleContext_CombinesParts()
    {
        var result = Context.AssembleContext(
            basePrompt: "You are a helper.",
            solvraMarkdown: "# Project Rules\nBe safe.",
            skills: ["Skill A content", "Skill B content"],
            lessons: ["## 2024-01-01 [test]\nLesson 1"],
            memoryFacts: "- User prefers Rust"
        );

        Assert.Contains("You are a helper.", result);
        Assert.Contains("# Project Rules", result);
        Assert.Contains("# Active Skills", result);
        Assert.Contains("Skill A content", result);
        Assert.Contains("# Lessons (relevant to this turn)", result);
        Assert.Contains("Lesson 1", result);
        Assert.Contains("# Memory", result);
        Assert.Contains("User prefers Rust", result);
        Assert.Contains("# Agent Instructions", result);
    }

    [Fact]
    public void AssembleContext_OmitsEmptySections()
    {
        var result = Context.AssembleContext(
            basePrompt: "Base",
            solvraMarkdown: null,
            skills: null,
            lessons: null,
            memoryFacts: null
        );

        Assert.Contains("Base", result);
        Assert.DoesNotContain("# Active Skills", result);
        Assert.DoesNotContain("# Lessons", result);
        Assert.DoesNotContain("# Memory", result);
    }

    [Fact]
    public void CompressContext_NoCompression_BelowThreshold()
    {
        // Create messages well below the 70% threshold for a 200k context model
        var messages = new List<Message>
        {
            Message.FromText(MessageRole.User, "Hello"),
            Message.FromText(MessageRole.Assistant, "Hi there")
        };

        var result = Context.CompressContext(messages, "claude-3-5-sonnet-20241022");

        // Should return original messages unchanged
        Assert.Equal(messages.Count, result.Count);
        Assert.Same(messages, result);
    }

    [Fact]
    public void CompressContext_MicroCompact_TruncatesToolResults()
    {
        // Create messages that would hit 70-85% of a small context window
        // Use a model with 128k context limit. 70% = ~89,600 tokens = ~358,400 chars
        var longContent = new string('x', 400_000);

        var messages = new List<Message>
        {
            Message.FromText(MessageRole.User, "Do something"),
            new()
            {
                Role = MessageRole.Tool,
                Content = [new ToolResultContent
                {
                    ToolUseId = "tc_1",
                    Content = longContent,
                    IsError = false
                }]
            }
        };

        var result = Context.CompressContext(messages, "llama3.1"); // 128k context
        Assert.Equal(2, result.Count);

        // The tool result should be truncated to 500 chars + compaction notice
        var toolMsg = result[1];
        var trContent = toolMsg.Content[0] as ToolResultContent;
        Assert.NotNull(trContent);
        Assert.True(trContent!.Content.Length < longContent.Length);
        Assert.Contains("compacted", trContent.Content);
    }

    [Fact]
    public void CompressContext_Truncate_KeepsFirstAndLast()
    {
        // Create 50 messages so truncation preserves first 2 + system message + last 30
        var messages = new List<Message>();
        for (int i = 0; i < 50; i++)
        {
            // Make each message ~25k chars = ~6250 tokens
            // 50 * 6250 = 312,500 tokens. For 128k context = ~244% → hard truncate
            messages.Add(Message.FromText(
                i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                new string((char)('a' + (i % 26)), 25_000)));
        }

        var result = Context.CompressContext(messages, "llama3.1");

        // Should have first 2 + compaction notice + last 30 = 33
        Assert.Equal(33, result.Count);

        // First two should be unchanged
        Assert.Equal(messages[0].GetTextContent(), result[0].GetTextContent());
        Assert.Equal(messages[1].GetTextContent(), result[1].GetTextContent());

        // Third should be the compaction notice
        Assert.Contains("Context compacted", result[2].GetTextContent());

        // Last should be the original last message
        Assert.Equal(messages[49].GetTextContent(), result[^1].GetTextContent());
    }
}
