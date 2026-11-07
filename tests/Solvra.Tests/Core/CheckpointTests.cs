#nullable enable

using System.Text.Json;
using Solvra.Core;
using Solvra.Hooks;
using Solvra.Models;
using Solvra.Observability;
using Solvra.Providers;
using Solvra.Security;
using Solvra.Tests.Harness;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Core;

/// <summary>stream-json checkpoints: the original of each edited file is saved before the edit.</summary>
public class CheckpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"solvra-ckpt-{Guid.NewGuid():N}");
    private string Checkpoints => Path.Combine(_dir, ".checkpoints");
    public CheckpointTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<LineSink> Run(ScriptedProvider provider, string mode = "auto", bool checkpoints = true)
    {
        var router = new ModelRouter(new Dictionary<string, Func<IProvider>> { ["fake"] = () => provider });
        var registry = new ToolRegistry();
        registry.RegisterTool(new FileEditTool());
        registry.RegisterTool(new FileWriteTool());
        var logs = Path.Combine(_dir, "logs");
        var loop = new AgentLoop(router, registry, new HookEngine(), new AuditLogger(logs),
            costTracker: new CostTracker(Path.Combine(_dir, "ledger.jsonl")),
            sessionManager: new SessionManager(Path.Combine(_dir, "sessions")),
            tracer: new Tracer(Path.Combine(logs, "traces.jsonl"), ObservabilityLevel.Off));
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake") { CheckpointDir = checkpoints ? Checkpoints : null };
        var hooks = stream.HooksFor("main");
        sink.OnEvent = ev =>
        {
            if (ev.GetProperty("type").GetString() == "permission_request")
                stream.HandleControl(JsonSerializer.Serialize(new { type = "permission_response", id = ev.GetProperty("id").GetString(), decision = "deny" }), new CancellationTokenSource());
        };
        await loop.RunAsync(new AgentRunOptions
        {
            Prompt = "go",
            Session = new SessionConfig { Id = "s", CreatedAt = "", Model = "m", Provider = "fake", PermissionMode = mode, MaxTurns = 5 },
            Cwd = _dir, LogToSession = false, Streaming = true, Observer = stream,
            OnToolCall = hooks.OnToolCall, OnToolResult = hooks.OnToolResult,
            OnPermissionRequest = mode == "auto" ? null : hooks.OnPermissionRequest,
        });
        return sink;
    }

    [Fact]
    public async Task Edit_SavesTheOriginal_AndReportsItOnTheResult()
    {
        var file = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(file, "one\n");
        var sink = await Run(new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_edit", new { path = "a.txt", old_string = "one", new_string = "two" }, id: "e1"))
            .Then(ScriptedProvider.Text("ok")));

        Assert.Equal("two\n", await File.ReadAllTextAsync(file));
        var cp = sink.OfType("tool_result").Single().GetProperty("checkpoint");
        Assert.Equal(file, cp.GetProperty("file").GetString());
        Assert.Equal("one\n", await File.ReadAllTextAsync(cp.GetProperty("before").GetString()!));
    }

    [Fact]
    public async Task NewFile_HasNoOriginal()
    {
        var sink = await Run(new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_write", new { path = "new.txt", content = "x" }, id: "w1"))
            .Then(ScriptedProvider.Text("ok")));
        var cp = sink.OfType("tool_result").Single().GetProperty("checkpoint");
        Assert.Equal(Path.Combine(_dir, "new.txt"), cp.GetProperty("file").GetString());
        Assert.Equal(JsonValueKind.Null, cp.GetProperty("before").ValueKind);
    }

    [Fact]
    public async Task DeniedEdit_HasNoCheckpoint_AndNoCheckpointDirMeansNone()
    {
        var file = Path.Combine(_dir, "b.txt");
        await File.WriteAllTextAsync(file, "one\n");
        var denied = await Run(new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_edit", new { path = "b.txt", old_string = "one", new_string = "two" }, id: "e2"))
            .Then(ScriptedProvider.Text("ok")), mode: "default");
        Assert.Equal(JsonValueKind.Null, denied.OfType("tool_result").Single().GetProperty("checkpoint").ValueKind);

        var off = await Run(new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_edit", new { path = "b.txt", old_string = "one", new_string = "two" }, id: "e3"))
            .Then(ScriptedProvider.Text("ok")), checkpoints: false);
        Assert.Equal(JsonValueKind.Null, off.OfType("tool_result").Single().GetProperty("checkpoint").ValueKind);
        Assert.False(Directory.Exists(Checkpoints) && Directory.GetFiles(Checkpoints).Any(f => Path.GetFileName(f).StartsWith("e3", StringComparison.Ordinal)));
    }

    [Fact]
    public void EnvironmentInfo_ListsAdditionalFolders()
    {
        var info = Context.BuildEnvironmentInfo(_dir, "m", ["/data/one", "/srv/two"]);
        Assert.Contains("Other folders the user added", info);
        Assert.Contains("    /data/one\n", info);
        Assert.DoesNotContain("Other folders", Context.BuildEnvironmentInfo(_dir, "m"));
    }
}
