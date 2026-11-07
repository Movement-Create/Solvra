#nullable enable

using Solvra.CLI;
using Solvra.Core;
using Solvra.Hooks;
using Solvra.Models;
using Solvra.Observability;
using Solvra.Providers;
using Solvra.Security;
using Solvra.Tests.Harness;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Security;

/// <summary>
/// Ask mode (permission mode "default") with nobody to approve — how the VS Code extension runs
/// `solvra run` — must never edit a file, run a command or start a subagent.
/// </summary>
public class AskModeSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"solvra-ask-{Guid.NewGuid():N}");

    public AskModeSafetyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AgentLoop Loop(ScriptedProvider provider)
    {
        var router = new ModelRouter(new Dictionary<string, Func<IProvider>> { ["fake"] = () => provider });
        var registry = new ToolRegistry();
        registry.RegisterTool(new FileReadTool());
        registry.RegisterTool(new FileWriteTool());
        registry.RegisterTool(new FileEditTool());
        registry.RegisterTool(new BashTool(new SandboxManager()));
        var logs = Path.Combine(_dir, "logs");
        return new AgentLoop(router, registry, new HookEngine(), new AuditLogger(logs),
            costTracker: new CostTracker(Path.Combine(_dir, "ledger.jsonl")),
            sessionManager: new SessionManager(Path.Combine(_dir, "sessions")),
            tracer: new Tracer(Path.Combine(logs, "traces.jsonl"), ObservabilityLevel.Off));
    }

    private AgentRunOptions Options(string mode, Action<ToolCall>? onCall = null, Action<ToolResult>? onResult = null) => new()
    {
        Prompt = "change it",
        Session = new SessionConfig
        {
            Id = "t", CreatedAt = DateTime.UtcNow.ToString("o"), Model = "m", Provider = "fake",
            PermissionMode = mode, MaxTurns = 10, MaxBudgetUsd = 100m
        },
        Cwd = _dir,
        LogToSession = false,
        OnPermissionRequest = null, // the extension has no approval channel yet
        OnToolCall = onCall,
        OnToolResult = onResult,
    };

    [Theory]
    [InlineData("default")]
    [InlineData("askall")]
    [InlineData("plan")]
    public async Task WithoutApproval_FileEditIsRefused_AndTheFileIsUnchanged(string mode)
    {
        var target = Path.Combine(_dir, "sample.js");
        await File.WriteAllTextAsync(target, "console.log('a');\n");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_edit", new { path = target, old_string = "'a'", new_string = "'b'" }))
            .Then(o =>
            {
                var tr = o.Messages[^1].Content.OfType<ToolResultContent>().Single();
                Assert.True(tr.IsError);
                return ScriptedProvider.Text("could not edit");
            });

        await Loop(provider).RunAsync(Options(mode));

        Assert.Equal("console.log('a');\n", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task AskMode_WithoutApproval_DoesNotRunCommands()
    {
        var marker = Path.Combine(_dir, "ran.txt");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("bash", new { command = $"echo ran > '{marker}'" }))
            .Then(ScriptedProvider.Text("done"));

        await Loop(provider).RunAsync(Options("default"));

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task AskMode_WithoutApproval_ReportsEachRefusedTool()
    {
        var target = Path.Combine(_dir, "new.txt");
        var err = new StringWriter();
        var (onCall, onResult) = AgentHost.ApprovalNeededReporter(err);
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_read", new { path = Path.Combine(_dir, "missing.txt") }, id: "r1"))
            .Then(ScriptedProvider.Tool("file_write", new { path = target, content = "x" }, id: "w1"))
            .Then(ScriptedProvider.Text("done"));

        await Loop(provider).RunAsync(Options("default", onCall, onResult));

        Assert.False(File.Exists(target));
        var lines = err.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
        Assert.Equal(new[] { $"{AgentHost.ApprovalNeededMarker} file_write" }, lines); // a failed read is not an approval refusal
    }

    [Fact]
    public async Task AutoMode_StillEdits()
    {
        var target = Path.Combine(_dir, "sample.js");
        await File.WriteAllTextAsync(target, "console.log('a');\n");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_edit", new { path = target, old_string = "'a'", new_string = "'b'" }))
            .Then(ScriptedProvider.Text("edited"));

        await Loop(provider).RunAsync(Options("auto"));

        Assert.Equal("console.log('b');\n", await File.ReadAllTextAsync(target));
    }

    [Theory]
    // --plan always wins, even over --auto.
    [InlineData(null, true, true, false, "auto", "plan")]
    [InlineData("auto", true, false, false, "default", "plan")]
    // An explicit --permission-mode beats --auto and the config default.
    [InlineData("default", false, true, false, "auto", "default")]
    [InlineData("ask", false, false, false, "auto", "default")]
    [InlineData("ask-all", false, false, false, "auto", "askall")]
    [InlineData("auto", false, false, false, "default", "auto")]
    // Legacy behaviour without the new flag.
    [InlineData(null, false, true, false, "default", "auto")]
    [InlineData(null, false, false, true, "default", "askall")]
    [InlineData(null, false, false, false, "auto", "auto")]
    [InlineData(null, false, false, false, "default", "default")]
    public void ResolvePermissionMode_Precedence(string? explicitMode, bool plan, bool auto, bool askAll, string config, string expected) =>
        Assert.Equal(expected, AgentHost.ResolvePermissionMode(explicitMode, plan, auto, askAll, config));

    [Fact]
    public void ParsePermissionMode_RejectsUnknownValues()
    {
        Assert.Null(AgentHost.ParsePermissionMode("yolo"));
        Assert.Null(AgentHost.ParsePermissionMode("bypasspermissions"));
    }
}
