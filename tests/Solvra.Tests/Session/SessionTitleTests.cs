#nullable enable

using Solvra.Core;
using Xunit;

namespace Solvra.Tests.Session;

public class SessionTitleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"solvra-titles-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("hi", "hi")]
    [InlineData("\n\n  fix   the\tbug  \nsecond line", "fix the bug")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void FromText_FirstLineCollapsed(string? text, string? expected) => Assert.Equal(expected, SessionTitles.FromText(text));

    [Fact]
    public void FromText_CapsLength()
    {
        var t = SessionTitles.FromText(new string('x', 200))!;
        Assert.Equal(SessionTitles.MaxLength, t.Length);
        Assert.EndsWith("…", t);
    }

    [Fact]
    public void Display_FallsBackToFirstUserMessage_ThenUntitled()
    {
        Assert.Equal("stored", SessionTitles.Display("stored", "hi"));
        Assert.Equal("hi", SessionTitles.Display(null, "hi"));
        Assert.Equal("Untitled", SessionTitles.Display(" ", null));
    }

    [Fact]
    public async Task Rename_AppendsAnEvent_AndListAndResumeAgree()
    {
        var mgr = new SessionManager(_dir);
        var untitled = await mgr.CreateAsync(new SessionConfig { Id = "s-untitled", CreatedAt = "", Model = "m", Provider = "p" });
        await mgr.LogUserMessageAsync(untitled, "hi");
        var named = await mgr.CreateAsync(new SessionConfig { Id = "s-named", CreatedAt = "", Title = "first title", Model = "m", Provider = "p" });
        var before = await File.ReadAllTextAsync(named.FilePath);

        await mgr.RenameAsync("s-named", "  Better   title ");

        var after = await File.ReadAllTextAsync(named.FilePath);
        Assert.StartsWith(before, after); // existing lines are untouched
        var rows = await mgr.ListSummariesAsync();
        Assert.Equal("hi", rows.Single(r => r.Id == "s-untitled").Title); // same rule as the VS Code sidebar
        Assert.Equal("Better title", rows.Single(r => r.Id == "s-named").Title);
        Assert.Equal("Better title", (await mgr.ResumeAsync("s-named")).Config.Title);
        await Assert.ThrowsAsync<FileNotFoundException>(() => mgr.RenameAsync("missing", "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => mgr.RenameAsync("s-named", "  "));
    }
}
