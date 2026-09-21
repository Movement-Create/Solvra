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
        // A custom base prompt replaces the default instructions.
        Assert.DoesNotContain("# Agent Instructions", result);
    }

    [Fact]
    public void AssembleContext_UsesCodingInstructionsByDefault()
    {
        var result = Context.AssembleContext(null, null, null, null, null, environment: "# Environment\n- Working directory: /x");
        Assert.Contains("# Agent Instructions", result);
        Assert.Contains("coding agent", result);
        Assert.Contains("Working directory: /x", result);
    }

    [Fact]
    public async Task LoadProjectInstructions_WalksUpToGitRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"solvra-ctx-{Guid.NewGuid():N}");
        var sub = Path.Combine(root, "src", "app");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "root agents rule");
            await File.WriteAllTextAsync(Path.Combine(root, "CLAUDE.md"), "root claude rule");
            await File.WriteAllTextAsync(Path.Combine(sub, "SOLVRA.md"), "sub rule");

            var text = await Context.LoadProjectInstructionsAsync(sub);
            Assert.NotNull(text);
            Assert.Contains("root agents rule", text);
            Assert.Contains("root claude rule", text);
            Assert.Contains("sub rule", text);
            Assert.True(text!.IndexOf("root agents rule", StringComparison.Ordinal) < text.IndexOf("sub rule", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
        // 50 exchanges of ~6k tokens each against a 128k window → must drop old exchanges.
        var messages = new List<Message>();
        for (int i = 0; i < 50; i++)
        {
            messages.Add(Message.FromText(
                i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                new string((char)('a' + (i % 26)), 25_000)));
        }

        var result = Context.CompressContext(messages, "llama3.1");

        Assert.True(result.Count < messages.Count);
        Assert.Equal(messages[0].GetTextContent(), result[0].GetTextContent());
        Assert.Contains("Context compacted", result[1].GetTextContent());
        Assert.Equal(messages[49].GetTextContent(), result[^1].GetTextContent());
        Assert.True(Context.EstimateContextTokens(result) < Context.GetContextLimit("llama3.1") * 0.6);
        // Roles still alternate after the notice (user, assistant(notice), user, ...).
        Assert.Equal(MessageRole.User, result[2].Role);
    }

    [Fact]
    public void CompressContext_NeverSplitsToolCallFromResult()
    {
        var messages = new List<Message> { Message.FromText(MessageRole.User, "task") };
        for (int i = 0; i < 40; i++)
        {
            messages.Add(new Message { Role = MessageRole.Assistant, Content = [new ToolUseContent { Id = $"c{i}", Name = "bash", Input = new() }] });
            // Large enough that even after shortening old results the history must be truncated.
            messages.Add(new Message { Role = MessageRole.Tool, Content = [new ToolResultContent { ToolUseId = $"c{i}", Content = new string('x', 30_000) }] });
            messages.Add(Message.FromText(MessageRole.Assistant, new string('y', 12_000)));
            messages.Add(Message.FromText(MessageRole.User, "continue"));
            if (i % 5 == 4)
            {
                messages.Add(Message.FromText(MessageRole.Assistant, "progress"));
                messages.Add(Message.FromText(MessageRole.User, $"next step {i}"));
            }
        }

        var result = Context.CompressContext(messages, "llama3.1");

        var toolUseIds = result.SelectMany(m => m.Content.OfType<ToolUseContent>()).Select(t => t.Id).ToHashSet();
        var resultIds = result.SelectMany(m => m.Content.OfType<ToolResultContent>()).Select(t => t.ToolUseId).ToHashSet();
        Assert.Equal(toolUseIds, resultIds);
        Assert.True(result.Count < messages.Count, "expected truncation");
        Assert.Contains("Context compacted", result[1].GetTextContent());
        Assert.Equal(MessageRole.User, result[2].Role);
        for (var i = 1; i < result.Count; i++)
            if (result[i].Role == MessageRole.Tool)
                Assert.Contains(result[i - 1].Content, c => c is ToolUseContent);
    }
}
