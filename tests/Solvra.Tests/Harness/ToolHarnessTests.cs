#nullable enable

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Solvra.Config;
using Solvra.Core;
using Solvra.Hooks;
using Solvra.Models;
using Solvra.Security;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Harness;

public class ToolHarnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"solvra-tools-{Guid.NewGuid():N}");

    public ToolHarnessTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ToolExecutionContext Ctx(string sessionId = "s") => new(
        sessionId, _dir, false, new Dictionary<string, string>(),
        new Solvra.Tools.SessionInfo(sessionId, "auto", new List<string>(), new List<string>()));

    private static JsonElement In(object o) => JsonSerializer.SerializeToElement(o);

    // ---------------- bash / sandbox

    [Fact]
    public async Task Bash_HugeOutput_DoesNotDeadlock_AndIsTruncated()
    {
        var sw = Stopwatch.StartNew();
        var r = await new BashTool(new SandboxManager()).ExecuteAsync(In(new { command = "head -c 30000000 /dev/zero | tr '\\0' 'a' | fold -w 100" }), Ctx());
        sw.Stop();
        Assert.False(r.IsError, r.Output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
        Assert.True(r.Output.Length < 40_000, $"output {r.Output.Length} chars");
        Assert.Contains("characters omitted", r.Output);
    }

    [Fact]
    public async Task Bash_TimeoutMs_IsHonoured()
    {
        var sw = Stopwatch.StartNew();
        var r = await new BashTool(new SandboxManager()).ExecuteAsync(In(new { command = "sleep 20", timeout_ms = 500 }), Ctx());
        Assert.True(r.IsError);
        Assert.Contains("timed out", r.Output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Bash_StdinIsClosed()
    {
        var sw = Stopwatch.StartNew();
        var r = await new BashTool(new SandboxManager()).ExecuteAsync(In(new { command = "cat; echo after", timeout_ms = 5000 }), Ctx());
        Assert.Contains("after", r.Output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Bash_DoesNotLeakSecrets()
    {
        Environment.SetEnvironmentVariable("SOLVRA_TEST_API_KEY", "sk-should-not-leak");
        Environment.SetEnvironmentVariable("SOLVRA_TEST_PLAIN", "visible");
        try
        {
            var r = await new BashTool(new SandboxManager()).ExecuteAsync(In(new { command = "env" }), Ctx());
            Assert.DoesNotContain("sk-should-not-leak", r.Output);
            Assert.Contains("SOLVRA_TEST_PLAIN=visible", r.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOLVRA_TEST_API_KEY", null);
            Environment.SetEnvironmentVariable("SOLVRA_TEST_PLAIN", null);
        }
    }

    [Theory]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("SOLVRA_WEBHOOK_SECRET", true)]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("OPENAI_BASE_URL", true)]
    [InlineData("PATH", false)]
    [InlineData("HOME", false)]
    [InlineData("SSH_AUTH_SOCK", false)]
    [InlineData("DOTNET_ROOT", false)]
    public void SecretNames(string name, bool secret) => Assert.Equal(secret, SandboxManager.IsSecretName(name));

    [Fact]
    public async Task Bash_ExitCodeIsReported()
    {
        var r = await new BashTool(new SandboxManager()).ExecuteAsync(In(new { command = "echo out; exit 3" }), Ctx());
        Assert.True(r.IsError);
        Assert.Contains("[exit code 3]", r.Output);
    }

    [Fact]
    public async Task Bash_Background_ReturnsPidAndLog()
    {
        var r = await new BashTool(new SandboxManager()).ExecuteAsync(In(new { command = "echo started; sleep 1", background = true }), Ctx());
        Assert.False(r.IsError, r.Output);
        Assert.Contains("PID", r.Output);
        Assert.Contains("solvra-bg", r.Output);
    }

    [Fact]
    public async Task CodeRun_BlocksDangerousBash_AndSanitizesFilename()
    {
        var tool = new CodeRunTool(new SandboxManager());
        var blocked = await tool.ExecuteAsync(In(new { language = "bash", code = "rm -rf ~" }), Ctx());
        Assert.True(blocked.IsError);
        Assert.Contains("Blocked", blocked.Output);

        var escaped = Path.Combine(_dir, "escape.py");
        var r = await tool.ExecuteAsync(In(new { language = "python", code = "print('hi')", filename = "../../" + Path.GetFileName(_dir) + "/escape.py" }), Ctx());
        Assert.Contains("hi", r.Output);
        Assert.False(File.Exists(escaped));
    }

    // ---------------- file tools

    [Fact]
    public async Task FileRead_DefaultLimit_AndPaging()
    {
        var path = Path.Combine(_dir, "big.txt");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 5000).Select(i => $"line {i}"));
        var r = await new FileReadTool().ExecuteAsync(In(new { path }), Ctx());
        Assert.Contains("1\tline 1", r.Output);
        Assert.DoesNotContain("line 2001\n", r.Output);
        Assert.Contains("Use offset=2001", r.Output);

        var page = await new FileReadTool().ExecuteAsync(In(new { path, offset = 4999, limit = 5 }), Ctx());
        Assert.Contains("4999\tline 4999", page.Output);
        Assert.Contains("5000\tline 5000", page.Output);
    }

    [Fact]
    public async Task FileRead_RefusesBinary_AndClipsLongLines()
    {
        var bin = Path.Combine(_dir, "b.bin");
        await File.WriteAllBytesAsync(bin, [1, 0, 2, 0, 3]);
        Assert.True((await new FileReadTool().ExecuteAsync(In(new { path = bin }), Ctx())).IsError);

        var min = Path.Combine(_dir, "min.js");
        await File.WriteAllTextAsync(min, new string('x', 50_000));
        var r = await new FileReadTool().ExecuteAsync(In(new { path = min }), Ctx());
        Assert.True(r.Output.Length < 3_000);
        Assert.Contains("line clipped", r.Output);
    }

    [Fact]
    public async Task FileEdit_ReplaceAll_CrLf_AndHints()
    {
        var path = Path.Combine(_dir, "f.cs");
        await File.WriteAllTextAsync(path, "a = 1;\r\n  b = 1;\r\na = 1;\r\n");
        var tool = new FileEditTool();

        var dup = await tool.ExecuteAsync(In(new { path, old_string = "a = 1;", new_string = "a = 2;" }), Ctx());
        Assert.True(dup.IsError);
        Assert.Contains("replace_all", dup.Output);

        var all = await tool.ExecuteAsync(In(new { path, old_string = "a = 1;", new_string = "a = 2;", replace_all = true }), Ctx());
        Assert.False(all.IsError, all.Output);
        Assert.Equal("a = 2;\r\n  b = 1;\r\na = 2;\r\n", await File.ReadAllTextAsync(path));

        var multi = await tool.ExecuteAsync(In(new { path, old_string = "  b = 1;\na = 2;", new_string = "  b = 3;\na = 2;" }), Ctx());
        Assert.False(multi.IsError, multi.Output); // \n in the request matches \r\n in the file
        Assert.Contains("b = 3;\r\n", await File.ReadAllTextAsync(path));

        var miss = await tool.ExecuteAsync(In(new { path, old_string = "b = 3;   \nnope", new_string = "x" }), Ctx());
        Assert.True(miss.IsError);
        Assert.Contains("line 2", miss.Output);
    }

    [Fact]
    public async Task Grep_Modes_AndMissingPath()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "src", "a.cs"), "class Foo {}\n// foo here\n");
        await File.WriteAllTextAsync(Path.Combine(_dir, "node_modules", "x.cs"), "class Foo {}\n");
        await File.WriteAllTextAsync(Path.Combine(_dir, ".gitignore"), "ignored/\n");
        Directory.CreateDirectory(Path.Combine(_dir, "ignored"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "ignored", "y.cs"), "class Foo {}\n");

        var grep = new GrepTool();
        var files = await grep.ExecuteAsync(In(new { pattern = "foo", case_insensitive = true, output_mode = "files" }), Ctx());
        Assert.Equal("src/a.cs", files.Output.Trim());

        var count = await grep.ExecuteAsync(In(new { pattern = "(?i)foo", output_mode = "count" }), Ctx());
        Assert.Contains("src/a.cs:2", count.Output);

        var missing = await grep.ExecuteAsync(In(new { pattern = "x", path = "does/not/exist" }), Ctx());
        Assert.True(missing.IsError);
        Assert.Contains("not found", missing.Output);

        var single = await grep.ExecuteAsync(In(new { pattern = "Foo", path = "src/a.cs" }), Ctx());
        Assert.Contains("a.cs:1:", single.Output);
    }

    [Fact]
    public async Task Glob_AnchoredPatterns_AndBraces()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src", "deep"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "top.ts"), "");
        await File.WriteAllTextAsync(Path.Combine(_dir, "src", "a.ts"), "");
        await File.WriteAllTextAsync(Path.Combine(_dir, "src", "deep", "b.tsx"), "");
        var glob = new GlobTool();

        var topOnly = await glob.ExecuteAsync(In(new { pattern = "src/*.ts" }), Ctx());
        Assert.Equal("src/a.ts", topOnly.Output.Trim());

        var braces = await glob.ExecuteAsync(In(new { pattern = "src/**/*.{ts,tsx}" }), Ctx());
        Assert.Contains("src/a.ts", braces.Output);
        Assert.Contains("src/deep/b.tsx", braces.Output);
        Assert.DoesNotContain("top.ts", braces.Output);

        var byName = await glob.ExecuteAsync(In(new { pattern = "*.tsx" }), Ctx());
        Assert.Equal("src/deep/b.tsx", byName.Output.Trim());
    }

    [Fact]
    public async Task Todo_IsPerSession()
    {
        var todo = new TodoTool();
        await todo.ExecuteAsync(In(new { action = "set", tasks = new[] { "one", "two" } }), Ctx("s1"));
        var other = await todo.ExecuteAsync(In(new { action = "list" }), Ctx("s2"));
        Assert.Equal("No tasks.", other.Output);
        var mine = await todo.ExecuteAsync(In(new { action = "list" }), Ctx("s1"));
        Assert.Contains("two", mine.Output);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("100.100.1.1", true)]
    [InlineData("::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    public void WebFetch_PrivateAddresses(string ip, bool isPrivate) =>
        Assert.Equal(isPrivate, WebFetchTool.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public async Task WebFetch_RefusesLocalhost()
    {
        var r = await new WebFetchTool().ExecuteAsync(In(new { url = "http://localhost:7331/trigger" }), Ctx());
        Assert.True(r.IsError);
        Assert.Contains("refusing", r.Output);
    }

    [Fact]
    public async Task AgentTool_EnforcesDepth()
    {
        var called = false;
        var previous = AgentTool.RunAgentDelegate;
        AgentTool.RunAgentDelegate = (_, _) => { called = true; return Task.FromResult("x"); };
        try
        {
            var r = await new AgentTool().ExecuteAsync(In(new { prompt = "go" }), Ctx() with { SubagentDepth = AgentTool.MaxSubagentDepth });
            Assert.True(r.IsError);
            Assert.False(called);
        }
        finally
        {
            AgentTool.RunAgentDelegate = previous;
        }
    }

    [Fact]
    public async Task UnknownTool_ListsAvailableTools()
    {
        var reg = new ToolRegistry();
        reg.RegisterTool(new GlobTool());
        var r = await reg.ExecuteToolAsync("globb", In(new { }), Ctx(), PermissionMode.Auto);
        Assert.True(r.IsError);
        Assert.Contains("glob", r.Output);
    }

    // ---------------- sessions, config, hooks

    [Fact]
    public async Task Session_AssistantTurns_RoundTripInOrder()
    {
        var mgr = new SessionManager(Path.Combine(_dir, "sessions"));
        var session = await mgr.CreateAsync(new SessionConfig { Id = "abc", CreatedAt = "" });
        await mgr.LogUserMessageAsync(session, "do it");
        await mgr.LogAssistantTurnAsync(session, new Message
        {
            Role = MessageRole.Assistant,
            Content = [new ReasoningContent { Text = "think" }, new TextContent { Text = "running" }, new ToolUseContent { Id = "c1", Name = "bash", Input = new() { ["command"] = JsonSerializer.SerializeToElement("ls") } }]
        });
        await mgr.AppendToolResultAsync(session, "c1", "file.txt", false);
        await mgr.LogAssistantMessageAsync(session, "done");
        await File.AppendAllTextAsync(session.FilePath, "{not json\n");

        var info = await mgr.ResumeAsync("abc");
        var roles = info.Messages.Select(m => m.Role).ToList();
        Assert.Equal([MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant], roles);
        var turn = info.Messages[1];
        Assert.Contains(turn.Content, c => c is TextContent { Text: "running" });
        Assert.Contains(turn.Content, c => c is ToolUseContent { Name: "bash" });
        Assert.Contains(turn.Content, c => c is ReasoningContent);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Session_RejectsPathLikeIds(string id)
    {
        var mgr = new SessionManager(Path.Combine(_dir, "sessions"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => mgr.DeleteAsync(id));
    }

    [Fact]
    public void Json5_KeepsUrlsInStrings()
    {
        var cleaned = ConfigLoader.StripJson5("""
            {
              // comment
              "system_prompt": "see https://example.com/docs", /* block */
              "model": "m",
            }
            """);
        using var doc = JsonDocument.Parse(cleaned);
        Assert.Equal("see https://example.com/docs", doc.RootElement.GetProperty("system_prompt").GetString());
    }

    [Fact]
    public async Task ShellHook_Exit2Blocks()
    {
        var hook = new ShellCommandHook(HookEvent.PreToolUse, "read x; echo 'no rm please' >&2; exit 2");
        var r = await hook.ExecuteAsync(new HookContext(HookEvent.PreToolUse, "s", 1, new ToolCallInfo("c", "bash", In(new { command = "rm x" }))));
        Assert.Equal(HookAction.Block, r.Action);
        Assert.Equal("no rm please", r.Reason);
    }

    [Fact]
    public async Task ShellHook_CanModifyInput()
    {
        var hook = new ShellCommandHook(HookEvent.PreToolUse, "cat >/dev/null; echo '{\"action\":\"modify\",\"input\":{\"command\":\"echo safe\"}}'");
        var r = await hook.ExecuteAsync(new HookContext(HookEvent.PreToolUse, "s", 1, new ToolCallInfo("c", "bash", In(new { command = "x" }))));
        Assert.Equal(HookAction.Modify, r.Action);
        Assert.Equal("echo safe", r.ModifiedInput!.Value.GetProperty("command").GetString());
    }
}
