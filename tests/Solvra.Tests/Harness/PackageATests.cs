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

/// <summary>Telemetry package A: run identity, request visibility, span join keys, result integrity and record ids.</summary>
public class PackageATests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"solvra-pkga-{Guid.NewGuid():N}");

    public PackageATests() { Directory.CreateDirectory(Path.Combine(_dir, "sessions")); Directory.CreateDirectory(Path.Combine(_dir, "work")); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (AgentLoop loop, SessionConfig session, string logs) Build(ScriptedProvider provider, string sessionId = "a")
    {
        var router = new ModelRouter(new Dictionary<string, Func<IProvider>> { ["fake"] = () => provider });
        var registry = new ToolRegistry();
        registry.RegisterTool(new FileReadTool());
        registry.RegisterTool(new FileWriteTool());
        registry.RegisterTool(new GlobTool());
        registry.RegisterTool(new GrepTool());
        var logs = Path.Combine(_dir, "logs");
        var loop = new AgentLoop(router, registry, new HookEngine(), new AuditLogger(logs),
            costTracker: new CostTracker(Path.Combine(_dir, "ledger.jsonl")),
            sessionManager: new SessionManager(Path.Combine(_dir, "sessions")),
            tracer: new Tracer(Path.Combine(logs, "traces.jsonl"), ObservabilityLevel.Normal));
        var session = new SessionConfig
        {
            Id = sessionId, CreatedAt = DateTime.UtcNow.ToString("o"), Model = "m", Provider = "fake",
            PermissionMode = "auto", MaxTurns = 10, MaxBudgetUsd = 100m,
            FilePath = Path.Combine(_dir, "sessions", $"{sessionId}.jsonl")
        };
        return (loop, session, logs);
    }

    private static List<JsonElement> Events(string file, string type) =>
        File.ReadAllLines(file).Where(l => l.Contains($"\"type\":\"{type}\"")).Select(l => JsonDocument.Parse(l).RootElement).ToList();

    private static string Sha256(string s) => ToolOutput.Sha256(s);

    [Fact]
    public async Task RunStart_RecordsIdentityInstructionFilesAndPromptOncePerSession()
    {
        var work = Path.Combine(_dir, "work");
        await File.WriteAllTextAsync(Path.Combine(work, "CLAUDE.md"), "rules\n");
        var provider = new ScriptedProvider().Then(ScriptedProvider.Text("ok")).Then(ScriptedProvider.Text("ok again"));
        var (loop, session, _) = Build(provider);
        var opts = new AgentRunOptions { Prompt = "go", Session = session, Cwd = work, LogToSession = true };

        await loop.RunAsync(opts);
        await loop.RunAsync(opts with { Prompt = "again" });

        var starts = Events(session.FilePath, "run_start");
        Assert.Equal(2, starts.Count);
        var d = starts[0].GetProperty("data");
        Assert.Equal("solvra", d.GetProperty("agent").GetString());
        Assert.False(string.IsNullOrEmpty(d.GetProperty("version").GetString()));
        Assert.Equal(16, d.GetProperty("trace_id").GetString()!.Length);
        Assert.Equal(work, d.GetProperty("cwd").GetString());
        var files = d.GetProperty("instruction_files");
        Assert.Equal(1, files.GetArrayLength());
        Assert.EndsWith("CLAUDE.md", files[0].GetProperty("path").GetString());
        Assert.Equal(Sha256("rules\n"), files[0].GetProperty("sha256").GetString());
        Assert.Equal(40, files[0].GetProperty("git_blob").GetString()!.Length);
        Assert.Equal(6, files[0].GetProperty("bytes").GetInt64());

        // the full prompt is stored once, hashed, and the hash on run_start matches it
        var prompts = Events(session.FilePath, "system_prompt");
        Assert.Single(prompts);
        var text = prompts[0].GetProperty("data").GetProperty("text").GetString()!;
        Assert.Contains("rules", text);
        Assert.Equal(Sha256(text), prompts[0].GetProperty("data").GetProperty("sha256").GetString());
        Assert.Equal(Sha256(text), d.GetProperty("system_prompt_sha256").GetString());
        Assert.Equal(64, d.GetProperty("tools_sha256").GetString()!.Length);
        Assert.Equal(4, d.GetProperty("tool_count").GetInt32());
    }

    [Fact]
    public async Task ToolResults_CarryIntegrityFieldsAndRecordIds()
    {
        var work = Path.Combine(_dir, "work");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_write", new { path = "a.txt", content = "abc\ndef\n" }, "w1"))
            .Then(ScriptedProvider.Tool("file_read", new { path = "a.txt" }, "r1"))
            .Then(ScriptedProvider.Tool("grep", new { pattern = "def", path = work }, "g1"))
            .Then(ScriptedProvider.Tool("glob", new { pattern = "*.txt", path = work }, "gl1"))
            .Then(ScriptedProvider.Text("done"));
        var (loop, session, logs) = Build(provider);
        await loop.RunAsync(new AgentRunOptions { Prompt = "go", Session = session, Cwd = work, LogToSession = true });

        var results = Events(session.FilePath, "tool_result").ToDictionary(e => e.GetProperty("data").GetProperty("call_id").GetString()!, e => e.GetProperty("data"));
        foreach (var r in results.Values)
        {
            Assert.Equal(64, r.GetProperty("output_sha256").GetString()!.Length);
            Assert.True(r.GetProperty("output_bytes").GetInt32() > 0);
            Assert.False(r.GetProperty("truncated").GetBoolean());
        }
        var read = results["r1"].GetProperty("records")[0];
        Assert.Equal("file", read.GetProperty("kind").GetString());
        Assert.Equal(Path.Combine(work, "a.txt"), read.GetProperty("path").GetString());
        Assert.Equal(Sha256("abc\ndef\n"), read.GetProperty("sha256").GetString());
        Assert.Equal("1-2", read.GetProperty("line_range").GetString());
        Assert.Equal(40, read.GetProperty("git_blob").GetString()!.Length);

        var grep = results["g1"].GetProperty("records");
        Assert.Equal(1, grep.GetArrayLength());
        Assert.Equal("match", grep[0].GetProperty("kind").GetString());
        Assert.Equal(2, grep[0].GetProperty("line").GetInt32());
        Assert.EndsWith("a.txt", grep[0].GetProperty("path").GetString());

        var glob = results["gl1"].GetProperty("records");
        Assert.Equal(1, glob.GetArrayLength());
        Assert.EndsWith("a.txt", glob[0].GetProperty("path").GetString());

        // the audit trail carries the trace id on every line and the same integrity fields on completion
        var audit = File.ReadAllLines(Path.Combine(logs, $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.All(audit, a => Assert.True(a.TryGetProperty("trace_id", out _), a.GetProperty("event_type").GetString()));
        var completed = audit.First(a => a.GetProperty("event_type").GetString() == "tool_call_completed" && a.GetProperty("data").GetProperty("operation_id").GetString() == "r1");
        Assert.Equal(1, completed.GetProperty("data").GetProperty("record_count").GetInt32());
        Assert.Equal(64, completed.GetProperty("data").GetProperty("output_sha256").GetString()!.Length);

        var started = audit.First(a => a.GetProperty("event_type").GetString() == "model_call_started").GetProperty("data");
        Assert.Equal(1, started.GetProperty("message_count").GetInt32());
        Assert.False(started.GetProperty("compressed").GetBoolean());
        Assert.Equal(0, started.GetProperty("dropped_messages").GetInt32());
        Assert.Equal(64, started.GetProperty("request_sha256").GetString()!.Length);
    }

    [Fact]
    public async Task Spans_CarryOperationIdsAndHonestTokenNames()
    {
        var work = Path.Combine(_dir, "work");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("glob", new { pattern = "*", path = work }, "c1"))
            .Then(ScriptedProvider.Text("done"));
        var (loop, session, logs) = Build(provider);
        await loop.RunAsync(new AgentRunOptions { Prompt = "go", Session = session, Cwd = work, LogToSession = false });

        var spans = File.ReadAllLines(Path.Combine(logs, "traces.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var sess = spans.First(s => s.GetProperty("type").GetString() == "span_start" && s.GetProperty("name").GetString() == "agent.session").GetProperty("attributes");
        Assert.Equal("solvra", sess.GetProperty("agent").GetString());
        Assert.Equal(work, sess.GetProperty("cwd").GetString());
        Assert.Equal(64, sess.GetProperty("system_prompt_sha256").GetString()!.Length);

        var llm = spans.First(s => s.GetProperty("type").GetString() == "span_start" && s.GetProperty("name").GetString() == "llm.call").GetProperty("attributes");
        Assert.StartsWith("llm-a-1-", llm.GetProperty("operation_id").GetString());
        Assert.True(llm.GetProperty("estimated_input_tokens").GetInt32() > 0);
        Assert.False(llm.TryGetProperty("input_tokens", out _));

        var resp = spans.First(s => s.GetProperty("type").GetString() == "span_event" && s.GetProperty("event_name").GetString() == "llm.response").GetProperty("event_attributes");
        Assert.Equal(10, resp.GetProperty("provider_input_tokens").GetInt32());

        var tool = spans.First(s => s.GetProperty("type").GetString() == "span_start" && s.GetProperty("name").GetString() == "tool.execute").GetProperty("attributes");
        Assert.Equal("c1", tool.GetProperty("operation_id").GetString());
    }

    [Fact]
    public void OutputCap_FlagsTruncationAndSpillsIntoTheGivenDirectory()
    {
        var ctx = new ToolExecutionContext("s", _dir, false, new Dictionary<string, string>(), new Solvra.Tools.SessionInfo("s", "auto", [], [])) { SpillDir = Path.Combine(_dir, "spill") };
        var text = string.Join("\n", Enumerable.Repeat(new string('x', 100), ToolOutput.DefaultMaxChars / 100 + 20)); // many short lines: the line clipper must not hide the cap
        var limited = ToolOutput.Limit(text, "t", ctx);
        Assert.True(ctx.Output.Truncated);
        Assert.Equal(text.Length, ctx.Output.OriginalChars);
        Assert.StartsWith(Path.Combine(_dir, "spill"), ctx.Output.SpillPath);
        Assert.Equal(text, File.ReadAllText(ctx.Output.SpillPath!));
        Assert.Contains("characters omitted", limited);

        var small = new ToolExecutionContext("s", _dir, false, new Dictionary<string, string>(), new Solvra.Tools.SessionInfo("s", "auto", [], []));
        ToolOutput.Limit("short", "t", small);
        Assert.False(small.Output.Truncated);
    }

    [Fact]
    public async Task AuditLog_RollsToTheEventDate()
    {
        var logs = Path.Combine(_dir, "audit");
        var logger = new AuditLogger(logs);
        var day = new DateTime(2026, 1, 1, 23, 59, 0, DateTimeKind.Utc);
        logger.Clock = () => day;
        await logger.LogAsync("a");
        logger.Clock = () => day.AddMinutes(2);
        await logger.LogAsync("b");
        await logger.DisposeAsync();
        Assert.Contains("\"event_type\":\"a\"", await File.ReadAllTextAsync(Path.Combine(logs, "audit-2026-01-01.jsonl")));
        Assert.Contains("\"event_type\":\"b\"", await File.ReadAllTextAsync(Path.Combine(logs, "audit-2026-01-02.jsonl")));
    }

    [Fact]
    public async Task Bash_ReportsExitCodeAndCommandRecord()
    {
        var tool = new BashTool(new SandboxManager());
        var ctx = new ToolExecutionContext("s", _dir, false, new Dictionary<string, string>(), new Solvra.Tools.SessionInfo("s", "auto", [], []));
        var ok = await tool.ExecuteAsync(JsonDocument.Parse("{\"command\":\"echo hi\"}").RootElement, ctx);
        Assert.Equal(0, ok.ExitCode);
        var rec = JsonDocument.Parse(JsonSerializer.Serialize(ok.Records![0])).RootElement;
        Assert.Equal("command", rec.GetProperty("kind").GetString());
        Assert.Equal(Sha256("echo hi"), rec.GetProperty("command_sha256").GetString());
        Assert.Equal(Sha256("hi\n"), rec.GetProperty("stdout_sha256").GetString());

        var bad = await tool.ExecuteAsync(JsonDocument.Parse("{\"command\":\"exit 3\"}").RootElement, ctx);
        Assert.Equal(3, bad.ExitCode);
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task CostLedger_SaysWhenCostIsUnknown()
    {
        var provider = new ScriptedProvider().Then(ScriptedProvider.Text("ok"));
        var (loop, session, _) = Build(provider);
        await loop.RunAsync(new AgentRunOptions { Prompt = "go", Session = session, Cwd = _dir, LogToSession = false });
        var entry = JsonDocument.Parse((await File.ReadAllLinesAsync(Path.Combine(_dir, "ledger.jsonl"))).Last()).RootElement;
        Assert.True(entry.GetProperty("cost_known").GetBoolean()); // the fake provider prices tokens
    }
}
