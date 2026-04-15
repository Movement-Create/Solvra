#nullable enable

using System.Text.Json;
using Solvra.Core;
using Solvra.Models;
using Xunit;

namespace Solvra.Tests.Session;

public class SessionManagerTests
{
    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"session_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task CreateAsync_WritesSessionStartEvent()
    {
        var dir = CreateTempDir();
        try
        {
            var mgr = new SessionManager(dir);
            var config = await mgr.CreateAsync(new SessionConfig
            {
                Id = "test-1",
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Model = "test-model"
            });

            Assert.Equal("test-1", config.Id);
            Assert.True(File.Exists(config.FilePath));

            var lines = await File.ReadAllLinesAsync(config.FilePath);
            Assert.Single(lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            Assert.Contains("session_start", lines[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AppendToolCallAsync_WritesToolCallEvent()
    {
        var dir = CreateTempDir();
        try
        {
            var mgr = new SessionManager(dir);
            var config = await mgr.CreateAsync(new SessionConfig
            {
                Id = "test-tool",
                CreatedAt = DateTime.UtcNow.ToString("o")
            });

            var input = JsonSerializer.SerializeToElement(new { path = "/tmp/test.txt" });
            await mgr.AppendToolCallAsync(config, "file_read", input, "call_123");

            var lines = (await File.ReadAllLinesAsync(config.FilePath))
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.Equal(2, lines.Length); // session_start + tool_call
            Assert.Contains("tool_call", lines[1]);
            Assert.Contains("file_read", lines[1]);
            Assert.Contains("call_123", lines[1]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AppendToolResultAsync_WritesToolResultEvent()
    {
        var dir = CreateTempDir();
        try
        {
            var mgr = new SessionManager(dir);
            var config = await mgr.CreateAsync(new SessionConfig
            {
                Id = "test-result",
                CreatedAt = DateTime.UtcNow.ToString("o")
            });

            await mgr.AppendToolResultAsync(config, "call_123", "file contents here", false);

            var lines = (await File.ReadAllLinesAsync(config.FilePath))
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Contains("tool_result", lines[1]);
            Assert.Contains("call_123", lines[1]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ResumeAsync_RestoresToolHistory()
    {
        var dir = CreateTempDir();
        try
        {
            var mgr = new SessionManager(dir);
            var config = await mgr.CreateAsync(new SessionConfig
            {
                Id = "test-resume",
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Model = "test-model"
            });

            // Write a user message
            await mgr.LogUserMessageAsync(config, "hello");

            // Write tool call + result
            var input = JsonSerializer.SerializeToElement(new { command = "ls" });
            await mgr.AppendToolCallAsync(config, "bash", input, "tc_001");
            await mgr.AppendToolResultAsync(config, "tc_001", "file1.txt\nfile2.txt", false);

            // Write assistant response
            await mgr.LogAssistantMessageAsync(config, "Here are the files.");

            // Resume and verify
            var info = await mgr.ResumeAsync("test-resume");
            Assert.Equal("test-model", info.Config.Model);

            // Messages: user + assistant(tool_use) + tool(tool_result) + assistant
            Assert.True(info.Messages.Count >= 3);

            // Check first message is user
            Assert.Equal(MessageRole.User, info.Messages[0].Role);
            Assert.Equal("hello", info.Messages[0].GetTextContent());

            // Check tool use content exists somewhere
            var hasToolUse = info.Messages.Any(m =>
                m.Content.Any(c => c is ToolUseContent tu && tu.Name == "bash"));
            Assert.True(hasToolUse);

            // Check tool result content exists
            var hasToolResult = info.Messages.Any(m =>
                m.Content.Any(c => c is ToolResultContent tr && tr.ToolUseId == "tc_001"));
            Assert.True(hasToolResult);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ResumeAsync_HandlesSessionWithNoToolEvents()
    {
        var dir = CreateTempDir();
        try
        {
            var mgr = new SessionManager(dir);
            var config = await mgr.CreateAsync(new SessionConfig
            {
                Id = "test-simple",
                CreatedAt = DateTime.UtcNow.ToString("o")
            });

            await mgr.LogUserMessageAsync(config, "hi");
            await mgr.LogAssistantMessageAsync(config, "hello!");

            var info = await mgr.ResumeAsync("test-simple");
            Assert.Equal(2, info.Messages.Count);
            Assert.Equal(MessageRole.User, info.Messages[0].Role);
            Assert.Equal(MessageRole.Assistant, info.Messages[1].Role);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesSessionFile()
    {
        var dir = CreateTempDir();
        try
        {
            var mgr = new SessionManager(dir);
            var config = await mgr.CreateAsync(new SessionConfig
            {
                Id = "test-delete",
                CreatedAt = DateTime.UtcNow.ToString("o")
            });

            Assert.True(File.Exists(config.FilePath));

            await mgr.DeleteAsync("test-delete");
            Assert.False(File.Exists(config.FilePath));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSessions()
    {
        var dir = CreateTempDir();
        try
        {
            var mgr = new SessionManager(dir);
            await mgr.CreateAsync(new SessionConfig { Id = "s1", CreatedAt = "2024-01-01T00:00:00Z" });
            await mgr.CreateAsync(new SessionConfig { Id = "s2", CreatedAt = "2024-01-02T00:00:00Z" });

            var sessions = await mgr.ListAsync();
            Assert.Equal(2, sessions.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ResumeAsync_ThrowsForMissingSession()
    {
        var dir = CreateTempDir();
        try
        {
            var mgr = new SessionManager(dir);
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => mgr.ResumeAsync("nonexistent"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
