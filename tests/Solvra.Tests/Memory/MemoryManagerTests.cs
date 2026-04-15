#nullable enable

using Solvra.Memory;
using Xunit;

namespace Solvra.Tests.Memory;

public class MemoryManagerTests
{
    private string CreateTempMemoryDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"memory_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task AppendAndSearchFact()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            await manager.AppendFactAsync("The API endpoint is at /v2/data");

            var results = await manager.SearchAsync("API");
            Assert.Contains("API endpoint", results);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AppendAndSearchLesson()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            await manager.AppendLessonAsync("Always check for null before accessing properties", new[] { "debugging", "null-safety" });

            var results = await manager.SearchAsync("null");
            Assert.Contains("null", results);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParsesLessonsCorrectly()
    {
        var content = @"
## 2024-01-15 [debugging, error-handling]
Always log the full stack trace

## 2024-01-20 [testing]
Use integration tests for database queries

## 2024-02-01
Plain lesson without tags
";

        var lessons = MemoryManager.ParseLessons(content);

        Assert.Equal(3, lessons.Count);

        Assert.Equal("2024-01-15", lessons[0].Date);
        Assert.Equal(2, lessons[0].Tags.Count);
        Assert.Contains("debugging", lessons[0].Tags);
        Assert.Contains("error-handling", lessons[0].Tags);
        Assert.Contains("stack trace", lessons[0].Content);

        Assert.Equal("2024-01-20", lessons[1].Date);
        Assert.Single(lessons[1].Tags);
        Assert.Contains("testing", lessons[1].Tags);

        Assert.Equal("2024-02-01", lessons[2].Date);
        Assert.Empty(lessons[2].Tags);
        Assert.Contains("Plain lesson", lessons[2].Content);
    }

    [Fact]
    public async Task GetRelevantLessonsScoresByTags()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);

            // Create lessons with different tags
            var lessonsContent = @"
## 2024-01-15 [python, debugging]
Use pdb for Python debugging

## 2024-01-20 [javascript, testing]
Use Jest for JavaScript testing

## 2024-02-01 [python, testing]
Use pytest for Python testing
";
            await File.WriteAllTextAsync(Path.Combine(dir, "lessons.md"), lessonsContent);

            // Search for python-related content
            var results = await manager.GetRelevantLessonsAsync("python debugging tips");

            Assert.NotEmpty(results);
            // The Python debugging lesson should be ranked highest (tag match = 3 for "python" + 3 for "debugging")
            Assert.Contains("pdb", results[0].Content);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetRelevantLessonsReturnsMax5()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            var content = "";
            for (var i = 0; i < 10; i++)
            {
                content += $"\n## 2024-01-{i + 1:D2} [code]\nLesson {i} about code\n";
            }
            await File.WriteAllTextAsync(Path.Combine(dir, "lessons.md"), content);

            var results = await manager.GetRelevantLessonsAsync("writing code");
            Assert.True(results.Count <= 5);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SearchesDailyLogs()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            // Fix 5d: daily logs are now in memory/ not memory/daily/
            await File.WriteAllTextAsync(
                Path.Combine(dir, "2024-01-15.md"),
                "## 10:00:00\nDeployed the new API version\n");

            var results = await manager.SearchAsync("deployed");
            Assert.Contains("Deployed", results);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ReturnsEmptyForNoMatches()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            var results = await manager.SearchAsync("nonexistent query");
            Assert.Empty(results);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // --- New tests for Fix 5 additions ---

    [Fact]
    public async Task WriteLessonsAsync_OverwritesWithBackup()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            var lessonsPath = Path.Combine(dir, "lessons.md");

            // Write initial content
            await File.WriteAllTextAsync(lessonsPath, "## 2024-01-01 [old]\nOld lesson\n");

            var lessons = new List<Lesson>
            {
                new("2024-02-01", new List<string> { "new" }, "New lesson 1"),
                new("2024-02-02", new List<string> { "new", "test" }, "New lesson 2")
            };

            await manager.WriteLessonsAsync(lessons);

            // Check backup was created
            Assert.True(File.Exists(lessonsPath + ".bak"));
            var backup = await File.ReadAllTextAsync(lessonsPath + ".bak");
            Assert.Contains("Old lesson", backup);

            // Check new content
            var content = await File.ReadAllTextAsync(lessonsPath);
            Assert.Contains("New lesson 1", content);
            Assert.Contains("New lesson 2", content);
            Assert.DoesNotContain("Old lesson", content);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LoadFactsAsync_ReturnsNull_WhenNoFile()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            var facts = await manager.LoadFactsAsync();
            Assert.Null(facts);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LoadFactsAsync_ReturnsContent()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            var factsPath = Path.Combine(dir, "facts.md");
            await File.WriteAllTextAsync(factsPath, "## 2024-01-01\nUser likes Rust\n");

            var facts = await manager.LoadFactsAsync();
            Assert.NotNull(facts);
            Assert.Contains("User likes Rust", facts);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LogConversationAsync_WritesToDailyLog()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            await manager.LogConversationAsync("What is Rust?", "Rust is a systems language.", "session-123");

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var logPath = Path.Combine(dir, $"{today}.md");
            Assert.True(File.Exists(logPath));

            var content = await File.ReadAllTextAsync(logPath);
            Assert.Contains("session-123", content);
            Assert.Contains("What is Rust?", content);
            Assert.Contains("Rust is a systems language.", content);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LogConversationAsync_TruncatesLongContent()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            var longPrompt = new string('a', 500);
            var longResponse = new string('b', 500);

            await manager.LogConversationAsync(longPrompt, longResponse, "session-456");

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var logPath = Path.Combine(dir, $"{today}.md");
            var content = await File.ReadAllTextAsync(logPath);

            // Both should be truncated to 200 chars
            Assert.DoesNotContain(longPrompt, content);
            Assert.DoesNotContain(longResponse, content);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RebuildIndexAsync_CreatesIndexJson()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "facts.md"), "## 2024-01-01\nUser prefers Rust and Python\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "lessons.md"), "## 2024-01-01 [testing]\nAlways write unit tests\n");

            await manager.RebuildIndexAsync();

            var indexPath = Path.Combine(dir, "index.json");
            Assert.True(File.Exists(indexPath));

            var content = await File.ReadAllTextAsync(indexPath);
            Assert.Contains("rust", content);
            Assert.Contains("python", content);
            Assert.Contains("testing", content);
            Assert.Contains("facts.md", content);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AppendToDailyLogAsync_UsesMemoryDir()
    {
        var dir = CreateTempMemoryDir();
        try
        {
            var manager = new MemoryManager(dir);
            await manager.AppendToDailyLogAsync("Test log entry", "session-1");

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            // Fix 5d: daily logs should be in memory/ not memory/daily/
            var logPath = Path.Combine(dir, $"{today}.md");
            Assert.True(File.Exists(logPath));

            var content = await File.ReadAllTextAsync(logPath);
            Assert.Contains("Test log entry", content);
            Assert.Contains("[session-1]", content);

            // Verify old path does NOT exist
            var oldPath = Path.Combine(dir, "daily", $"{today}.md");
            Assert.False(File.Exists(oldPath));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task WriteLessonsAsync_CreatesDirectoryIfNeeded()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"memory_test_new_{Guid.NewGuid():N}");
        try
        {
            var manager = new MemoryManager(dir);
            var lessons = new List<Lesson>
            {
                new("2024-01-01", new List<string> { "test" }, "Test content")
            };

            await manager.WriteLessonsAsync(lessons);

            var path = Path.Combine(dir, "lessons.md");
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
