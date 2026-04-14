#nullable enable

using Solvra.Skills;
using Xunit;

namespace Solvra.Tests.Skills;

public class SkillLoaderTests
{
    [Fact]
    public void WordBoundaryMatchingWorks()
    {
        var patterns = new List<string> { "deploy", "test" };

        // Exact word match
        Assert.True(SkillLoader.MatchesTriggerPatterns(patterns, "run the deploy"));
        Assert.True(SkillLoader.MatchesTriggerPatterns(patterns, "test the code"));

        // Word boundary prevents partial matches
        Assert.False(SkillLoader.MatchesTriggerPatterns(patterns, "deployment is ready"));
        Assert.False(SkillLoader.MatchesTriggerPatterns(patterns, "testing the code"));
        Assert.False(SkillLoader.MatchesTriggerPatterns(patterns, "contest results"));
    }

    [Fact]
    public void CaseInsensitiveMatching()
    {
        var patterns = new List<string> { "Deploy" };
        Assert.True(SkillLoader.MatchesTriggerPatterns(patterns, "run the deploy"));
        Assert.True(SkillLoader.MatchesTriggerPatterns(patterns, "run the DEPLOY"));
    }

    [Fact]
    public void SpecialCharactersInPatternsAreEscaped()
    {
        // c++ contains non-word chars so \b won't wrap it cleanly.
        // The pattern should still match literally (escaped +) but word boundary
        // behavior is regex-defined: \b fires at word/non-word transitions.
        var patterns = new List<string> { "c++" };
        // "c++" appears as a standalone token preceded by a space (word boundary before 'c')
        Assert.True(SkillLoader.MatchesTriggerPatterns(patterns, "compile c++ code"));
        // "cpp" does not contain "++" so it shouldn't match
        Assert.False(SkillLoader.MatchesTriggerPatterns(patterns, "compile cpp code"));
    }

    [Fact]
    public void EmptyPatternsReturnsFalse()
    {
        var patterns = new List<string>();
        Assert.False(SkillLoader.MatchesTriggerPatterns(patterns, "anything"));
    }

    [Fact]
    public void ParsesFrontmatterCorrectly()
    {
        var content = @"---
name: test-skill
description: A test skill
trigger_patterns: [""deploy"", ""test""]
always_inject: false
---
This is the skill content.";

        var (frontmatter, body) = SkillLoader.ParseFrontmatter(content);

        Assert.Equal("test-skill", frontmatter["name"]);
        Assert.Equal("A test skill", frontmatter["description"]);
        Assert.Contains("deploy", frontmatter["trigger_patterns"]);
        Assert.Equal("false", frontmatter["always_inject"]);
        Assert.Contains("This is the skill content.", body);
    }

    [Fact]
    public void ParsesFrontmatterWithNoDelimiters()
    {
        var content = "Just plain content";
        var (frontmatter, body) = SkillLoader.ParseFrontmatter(content);

        Assert.Empty(frontmatter);
        Assert.Equal("Just plain content", body);
    }

    [Fact]
    public async Task LoadsSkillsFromDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"skills_test_{Guid.NewGuid():N}");
        var skillDir = Path.Combine(tempDir, "my-skill");
        Directory.CreateDirectory(skillDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), @"---
name: my-skill
description: A test skill
trigger_patterns: [""deploy""]
always_inject: false
---
Skill instructions here.");

            var loader = new SkillLoader(tempDir);
            var skills = await loader.GetAllSkillsAsync();

            Assert.Single(skills);
            Assert.Equal("my-skill", skills[0].Name);
            Assert.Equal("A test skill", skills[0].Description);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GetRelevantSkillsFilters()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"skills_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "deploy-skill"));
        Directory.CreateDirectory(Path.Combine(tempDir, "test-skill"));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "deploy-skill", "SKILL.md"), @"---
name: deploy-skill
description: Deploy skill
trigger_patterns: [""deploy""]
---
Deploy instructions.");

            await File.WriteAllTextAsync(Path.Combine(tempDir, "test-skill", "SKILL.md"), @"---
name: test-skill
description: Test skill
trigger_patterns: [""test""]
---
Test instructions.");

            var loader = new SkillLoader(tempDir);
            var skills = await loader.GetRelevantSkillsAsync("run the deploy");

            Assert.Single(skills);
            Assert.Equal("deploy-skill", skills[0].Name);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
