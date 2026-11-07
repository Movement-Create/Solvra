#nullable enable

using Solvra.Security;
using Xunit;

namespace Solvra.Tests.Security;

/// <summary>Program lookup for macOS/Linux and Windows, with a fake environment.</summary>
public class ShellLocatorTests
{
    private static Func<string, string?> Env(params (string, string)[] vars)
    {
        var d = vars.ToDictionary(v => v.Item1, v => (string?)v.Item2);
        return k => d.TryGetValue(k, out var v) ? v : null;
    }

    private static Func<string, bool> Files(params string[] paths) => p => paths.Contains(p);

    [Fact]
    public void Unix_UsesBinBash()
    {
        Assert.Equal("/bin/bash", ShellLocator.ResolveBash(false, Env(), Files("/bin/bash")));
        Assert.Equal("/x/bash", ShellLocator.ResolveBash(false, Env(("SOLVRA_BASH", "/x/bash")), Files()));
    }

    [Fact]
    public void Windows_PrefersGitBash_AndSkipsWslBash()
    {
        var gitBash = Path.Combine(@"C:\Program Files", "Git", "bin", "bash.exe");
        var wsl = Path.Combine(@"C:\Windows\System32", "bash.exe");
        Assert.Equal(gitBash, ShellLocator.ResolveBash(true, Env(("ProgramFiles", @"C:\Program Files"), ("PATH", @"C:\Windows\System32")), Files(gitBash, wsl)));
        Assert.Null(ShellLocator.ResolveBash(true, Env(("PATH", @"C:\Windows\System32")), Files(wsl)));
        var userGit = Path.Combine(@"C:\Users\a\AppData\Local", "Programs", "Git", "bin", "bash.exe");
        Assert.Equal(userGit, ShellLocator.ResolveBash(true, Env(("LOCALAPPDATA", @"C:\Users\a\AppData\Local")), Files(userGit)));
    }

    [Fact]
    public void Windows_NpmCodexShim_RunsThroughCmd()
    {
        var shim = Path.Combine(@"C:\Users\a\AppData\Roaming", "npm", "codex.cmd");
        var launch = ShellLocator.ResolveCodex(true, Env(("APPDATA", @"C:\Users\a\AppData\Roaming"), ("SystemRoot", @"C:\Windows")), Files(shim), @"C:\Users\a");
        Assert.EndsWith("cmd.exe", launch.FileName);
        Assert.Equal(new[] { "/d", "/c", shim }, launch.PrefixArgs);

        var exe = Path.Combine(@"C:\tools", "codex.exe");
        var direct = ShellLocator.ResolveCodex(true, Env(("PATH", @"C:\tools")), Files(exe), @"C:\Users\a");
        Assert.Equal(exe, direct.FileName);
        Assert.Empty(direct.PrefixArgs);
    }

    [Fact]
    public void Unix_FindsCodexOnPathOrHomebrew_ElseBareName()
    {
        Assert.Equal("/opt/homebrew/bin/codex", ShellLocator.ResolveCodex(false, Env(("PATH", "/usr/bin")), Files("/opt/homebrew/bin/codex"), "/Users/a").FileName);
        Assert.Equal("/usr/bin/codex", ShellLocator.ResolveCodex(false, Env(("PATH", "/usr/bin")), Files("/usr/bin/codex"), "/Users/a").FileName);
        Assert.Equal("/x/codex", ShellLocator.ResolveCodex(false, Env(("SOLVRA_CODEX_BIN", "/x/codex")), Files("/x/codex"), "/Users/a").FileName);
        Assert.Equal("codex", ShellLocator.ResolveCodex(false, Env(), Files(), "/Users/a").FileName);
    }
}
