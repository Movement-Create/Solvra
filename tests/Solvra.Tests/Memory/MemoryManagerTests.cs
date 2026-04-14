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
        var dailyDir = Path.Combine(dir, "daily");
        Directory.CreateDirectory(dailyDir);
        try
        {
            var manager = new MemoryManager(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dailyDir, "2024-01-15.md"),
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
}
