#nullable enable

using System.Text.Json;
using Solvra.Core;
using Solvra.Hooks;
using Solvra.Models;
using Solvra.Observability;
using Solvra.Providers;
using Solvra.Security;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Harness;

/// <summary>The run_end session event summarises decisions, verifications and written files.</summary>
public class RunEndRecordTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"solvra-runend-{Guid.NewGuid():N}");

    public RunEndRecordTests() { Directory.CreateDirectory(Path.Combine(_dir, "sessions")); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (AgentLoop loop, SessionConfig session) Build(ScriptedProvider provider)
    {
        var router = new ModelRouter(new Dictionary<string, Func<IProvider>> { ["fake"] = () => provider });
        var registry = new ToolRegistry();
        registry.RegisterTool(new FileWriteTool());
        registry.RegisterTool(new DecideTool());
        registry.RegisterTool(new VerifyTool());
        var logs = Path.Combine(_dir, "logs");
        var loop = new AgentLoop(router, registry, new HookEngine(), new AuditLogger(logs),
            costTracker: new CostTracker(Path.Combine(_dir, "ledger.jsonl")),
            sessionManager: new SessionManager(Path.Combine(_dir, "sessions")),
            tracer: new Tracer(Path.Combine(logs, "traces.jsonl"), "t", ObservabilityLevel.Normal));
        var session = new SessionConfig
        {
            Id = "t", CreatedAt = DateTime.UtcNow.ToString("o"), Model = "m", Provider = "fake",
            PermissionMode = "auto", MaxTurns = 10, MaxBudgetUsd = 100m,
            FilePath = Path.Combine(_dir, "sessions", "t.jsonl")
        };
        return (loop, session);
    }

    private static JsonElement RunEnd(string sessionFile)
    {
        var line = File.ReadAllLines(sessionFile).Single(l => l.Contains("\"type\":\"run_end\""));
        return JsonDocument.Parse(line).RootElement.GetProperty("data");
    }

    [Fact]
    public async Task RunEnd_CarriesDecisionsVerificationsAndArtifacts()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("decide", new { question = "which?", options = new[] { "a", "b" }, choice = "b", confidence = 0.8 }, "d1"))
            .Then(ScriptedProvider.Tool("file_write", new { path = "out.txt", content = "hello" }, "w1"))
            .Then(ScriptedProvider.Tool("verify", new { check = "cat out.txt", passed = true, attempt = 2, command_or_call_id = "w1" }, "v1"))
            .Then(ScriptedProvider.Text("done"));
        var (loop, session) = Build(provider);

        var result = await loop.RunAsync(new AgentRunOptions { Prompt = "go", Session = session, Cwd = _dir, LogToSession = true });

        Assert.Equal(StopReason.Text, result.StopReason);
        var end = RunEnd(session.FilePath);
        Assert.Equal("text", end.GetProperty("stop_reason").GetString());
        Assert.Equal(4, end.GetProperty("turns").GetInt32());
        Assert.Equal(16, end.GetProperty("trace_id").GetString()!.Length);
        Assert.Equal(1, end.GetProperty("decisions").GetInt32());

        var v = end.GetProperty("verifications");
        Assert.Equal(1, v.GetArrayLength());
        Assert.Equal("cat out.txt", v[0].GetProperty("check").GetString());
        Assert.True(v[0].GetProperty("passed").GetBoolean());
        Assert.Equal(2, v[0].GetProperty("attempt").GetInt32());
        Assert.Equal("v1", v[0].GetProperty("call_id").GetString());

        var a = end.GetProperty("artifacts");
        Assert.Equal(1, a.GetArrayLength());
        Assert.Equal(Path.Combine(_dir, "out.txt"), a[0].GetProperty("path").GetString());
        // the file did not exist before the call; session events omit null properties
        Assert.False(a[0].TryGetProperty("sha256_before", out var before) && before.ValueKind != JsonValueKind.Null);
        Assert.Equal(64, a[0].GetProperty("sha256_after").GetString()!.Length);
        Assert.Equal("w1", a[0].GetProperty("call_id").GetString());

        // usage is the sum over four model calls of the scripted 10/5 tokens; cost is non-zero so it is known
        Assert.Equal(40, end.GetProperty("usage").GetProperty("input").GetInt32());
        Assert.True(end.GetProperty("cost_known").GetBoolean());

        var audit = await File.ReadAllTextAsync(Path.Combine(_dir, "logs", $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl"));
        Assert.Contains("\"decisions\":1", audit);
    }

    [Fact]
    public async Task RunEnd_IsWrittenOnErrorToo()
    {
        var provider = new ScriptedProvider().Throw(new InvalidOperationException("boom"));
        var (loop, session) = Build(provider);
        var result = await loop.RunAsync(new AgentRunOptions { Prompt = "go", Session = session, Cwd = _dir, LogToSession = true });
        Assert.Equal(StopReason.Error, result.StopReason);
        var end = RunEnd(session.FilePath);
        Assert.Equal("error", end.GetProperty("stop_reason").GetString());
        Assert.Equal(0, end.GetProperty("decisions").GetInt32());
    }

    [Fact]
    public void DefaultInstructions_MentionDecideAndVerify()
    {
        var prompt = Context.AssembleContext(null, null, null, null, null, "");
        Assert.Contains("decide tool", prompt);
        Assert.Contains("verify tool", prompt);
    }
}
