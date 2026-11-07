#nullable enable

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Solvra.CLI;
using Solvra.Config;
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

/// <summary>Collects stream-json lines and lets a test react to each one (e.g. answer a permission request).</summary>
internal sealed class LineSink : TextWriter
{
    private readonly StringBuilder _buf = new();
    public readonly ConcurrentQueue<JsonElement> Events = new();
    public Action<JsonElement>? OnEvent;
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value)
    {
        if (value != '\n') { _buf.Append(value); return; }
        var line = _buf.ToString(); _buf.Clear();
        var ev = JsonDocument.Parse(line).RootElement.Clone();
        Events.Enqueue(ev);
        OnEvent?.Invoke(ev);
    }
    public List<JsonElement> OfType(string type) => Events.Where(e => e.GetProperty("type").GetString() == type).ToList();
    public List<string> Types() => Events.Select(e => e.GetProperty("type").GetString()!).ToList();
}

/// <summary>Answers by inspecting the conversation, so parallel subagents get deterministic replies.</summary>
internal sealed class RoutingProvider : IProvider
{
    private readonly Func<CompletionOptions, LlmResponse> _route;
    public RoutingProvider(Func<CompletionOptions, LlmResponse> route) => _route = route;
    public string Id => "fake";
    public string DisplayName => "Fake";
    public Task<LlmResponse> CompleteAsync(CompletionOptions options, CancellationToken ct = default) => Task.FromResult(_route(options));
    public async IAsyncEnumerable<StreamEvent> StreamAsync(CompletionOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await CompleteAsync(options, ct);
        if (!string.IsNullOrEmpty(r.Reasoning)) yield return new StreamReasoning(r.Reasoning);
        if (!string.IsNullOrEmpty(r.Text)) yield return new StreamText(r.Text);
        foreach (var tc in r.ToolCalls)
        {
            yield return new StreamToolUseStart(tc.Id, tc.Name);
            yield return new StreamToolUseDelta(tc.Id, JsonSerializer.Serialize(tc.Input));
            yield return new StreamToolUseEnd(tc.Id);
        }
        yield return new StreamMessageEnd(r.Usage, r.StopReason);
    }
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(["m"]);
    public Task<bool> ValidateAsync(CancellationToken ct = default) => Task.FromResult(true);
    public decimal EstimateCost(string model, int inputTokens, int outputTokens) => (inputTokens + outputTokens) / 1_000_000m;
}

public class StreamJsonRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"solvra-stream-{Guid.NewGuid():N}");
    public StreamJsonRunTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (AgentLoop Loop, ToolRegistry Registry, ModelRouter Router) Build(IProvider provider)
    {
        var router = new ModelRouter(new Dictionary<string, Func<IProvider>> { ["fake"] = () => provider });
        var registry = new ToolRegistry();
        registry.RegisterTool(new FileReadTool());
        registry.RegisterTool(new FileWriteTool());
        registry.RegisterTool(new FileEditTool());
        registry.RegisterTool(new BashTool(new SandboxManager()));
        registry.RegisterTool(new TodoTool());
        var logs = Path.Combine(_dir, "logs");
        var loop = new AgentLoop(router, registry, new HookEngine(), new AuditLogger(logs),
            costTracker: new CostTracker(Path.Combine(_dir, "ledger.jsonl")),
            sessionManager: new SessionManager(Path.Combine(_dir, "sessions")),
            tracer: new Tracer(Path.Combine(logs, "traces.jsonl"), ObservabilityLevel.Off));
        return (loop, registry, router);
    }

    private AgentRunOptions Options(StreamJsonRun stream, string mode = "default", string sessionId = "t")
    {
        var hooks = stream.HooksFor("main");
        return new AgentRunOptions
        {
            Prompt = "go",
            Session = new SessionConfig
            {
                Id = sessionId, CreatedAt = DateTime.UtcNow.ToString("o"), Model = "m", Provider = "fake",
                PermissionMode = mode, MaxTurns = 10, MaxBudgetUsd = 100m
            },
            Cwd = _dir,
            LogToSession = false,
            Streaming = true,
            OnText = hooks.OnText,
            OnThinking = hooks.OnThinking,
            OnToolCall = hooks.OnToolCall,
            OnToolResult = hooks.OnToolResult,
            OnUsage = hooks.OnUsage,
            OnPermissionRequest = mode == "auto" ? null : hooks.OnPermissionRequest,
            Observer = stream,
        };
    }

    private static void Answer(StreamJsonRun stream, JsonElement ev, string decision, string? message = null)
    {
        var id = ev.GetProperty("id").GetString();
        stream.HandleControl(JsonSerializer.Serialize(new { type = "permission_response", id, decision, message }), new CancellationTokenSource());
    }

    // ── Diff ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnifiedDiff_OneChangedLine()
    {
        var d = UnifiedDiff.Create("a\nb\nc\nd\ne\nf\ng\nh\n", "a\nb\nc\nd\nE\nf\ng\nh\n", "x.txt");
        Assert.Equal(1, d.Added);
        Assert.Equal(1, d.Removed);
        Assert.Equal("--- a/x.txt\n+++ b/x.txt\n@@ -2,7 +2,7 @@\n b\n c\n d\n-e\n+E\n f\n g\n h\n", d.Text);
    }

    [Fact]
    public void UnifiedDiff_NewFileAndNoChange()
    {
        var created = UnifiedDiff.Create(null, "one\ntwo\n", "n.txt");
        Assert.StartsWith("--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1,2 @@\n+one\n+two\n", created.Text);
        Assert.Equal((2, 0), (created.Added, created.Removed));
        Assert.Equal("", UnifiedDiff.Create("same\n", "same\n", "s.txt").Text);
    }

    [Fact]
    public void UnifiedDiff_DistantChangesMakeTwoHunks()
    {
        var old = string.Join("\n", Enumerable.Range(1, 30)) + "\n";
        var updated = old.Replace("\n2\n", "\ntwo\n").Replace("\n28\n", "\ntwenty-eight\n");
        var d = UnifiedDiff.Create(old, updated, "n.txt");
        Assert.Equal(2, d.Text.Split('\n').Count(l => l.StartsWith("@@")));
    }

    // ── Rules ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("node check.js", "node")]
    [InlineData("  npm test ", "npm")]
    [InlineData("node a.js && rm -rf x", null)]
    [InlineData("cat x | sh", null)]
    [InlineData("echo $(whoami)", null)]
    [InlineData("FOO=1 node a.js", null)]
    [InlineData("", null)]
    public void CommandPrefix_OnlyForSimpleCommands(string command, string? expected) =>
        Assert.Equal(expected, StreamJsonRun.CommandPrefix(command));

    [Theory]
    [InlineData("edit", "edit")]
    [InlineData("execute:node", "execute:node")]
    [InlineData("execute", null)]
    [InlineData("execute:node x", null)]
    [InlineData("edit:foo", null)]
    [InlineData("root", null)]
    public void PermissionRule_Parse(string text, string? expected) =>
        Assert.Equal(expected, StreamJsonRun.PermissionRule.Parse(text)?.ToString());

    // ── Approvals ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskMode_EditWaitsForApproval_ThenAppliesWithDiffAndMeta()
    {
        var target = Path.Combine(_dir, "sample.js");
        await File.WriteAllTextAsync(target, "const a = 1;\nconsole.log(a);\n");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_edit", new { path = target, old_string = "1", new_string = "2" }))
            .Then(ScriptedProvider.Text("Edited."));
        var (loop, registry, _) = Build(provider);
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake");
        sink.OnEvent = ev =>
        {
            if (ev.GetProperty("type").GetString() != "permission_request") return;
            Assert.Equal("1 const a = 1;", "1 " + File.ReadAllLines(target)[0]); // not applied before the answer
            Answer(stream, ev, "allow");
        };

        await loop.RunAsync(Options(stream));

        Assert.Equal("const a = 2;\nconsole.log(a);\n", await File.ReadAllTextAsync(target));
        var types = sink.Types();
        Assert.True(types.IndexOf("tool_call") < types.IndexOf("permission_request"));
        Assert.True(types.IndexOf("permission_request") < types.IndexOf("tool_result"));
        var req = sink.OfType("permission_request").Single();
        Assert.Equal("edit", req.GetProperty("kind").GetString());
        Assert.Equal("Edit sample.js", req.GetProperty("summary").GetString());
        Assert.Contains("-const a = 1;\n+const a = 2;", req.GetProperty("diff").GetString());
        Assert.Equal("edit", req.GetProperty("always").GetString());
        var result = sink.OfType("tool_result").Single();
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(1, result.GetProperty("meta").GetProperty("added").GetInt32());
        Assert.Equal(1, result.GetProperty("meta").GetProperty("removed").GetInt32());
        Assert.Contains(sink.OfType("text_delta"), e => e.GetProperty("text").GetString() == "Edited.");
        Assert.NotEmpty(sink.OfType("usage"));
    }

    [Fact]
    public async Task Deny_WithMessage_LeavesFileAndTellsTheModel()
    {
        var target = Path.Combine(_dir, "sample.js");
        await File.WriteAllTextAsync(target, "x\n");
        string? seen = null;
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_edit", new { path = target, old_string = "x", new_string = "y" }))
            .Then(o =>
            {
                seen = o.Messages[^1].Content.OfType<ToolResultContent>().Single().Content;
                return ScriptedProvider.Text("What should I do instead?");
            });
        var (loop, registry, _) = Build(provider);
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake");
        sink.OnEvent = ev => { if (ev.GetProperty("type").GetString() == "permission_request") Answer(stream, ev, "deny", "rename the variable instead"); };

        await loop.RunAsync(Options(stream));

        Assert.Equal("x\n", await File.ReadAllTextAsync(target));
        Assert.Contains("denied", seen);
        Assert.Contains("The user said: rename the variable instead", seen);
        Assert.False(sink.OfType("tool_result").Single().GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task AllowAlways_ForEdits_SkipsLaterEditPrompts_AndReportsTheRule()
    {
        var a = Path.Combine(_dir, "a.txt"); var b = Path.Combine(_dir, "b.txt");
        await File.WriteAllTextAsync(a, "1\n"); await File.WriteAllTextAsync(b, "1\n");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_edit", new { path = a, old_string = "1", new_string = "2" }, id: "e1"))
            .Then(ScriptedProvider.Tool("file_edit", new { path = b, old_string = "1", new_string = "2" }, id: "e2"))
            .Then(ScriptedProvider.Text("done"));
        var (loop, registry, _) = Build(provider);
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake");
        sink.OnEvent = ev => { if (ev.GetProperty("type").GetString() == "permission_request") Answer(stream, ev, "allow_always"); };

        await loop.RunAsync(Options(stream));

        Assert.Equal("2\n", await File.ReadAllTextAsync(a));
        Assert.Equal("2\n", await File.ReadAllTextAsync(b));
        Assert.Single(sink.OfType("permission_request"));
        Assert.Equal("edit", sink.OfType("permission_rule").Single().GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ExecuteRule_FromCommandLine_AllowsOnlyThatSimpleCommand()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("bash", new { command = "echo allowed" }, id: "b1"))
            .Then(ScriptedProvider.Tool("bash", new { command = "echo x; echo chained" }, id: "b2"))
            .Then(ScriptedProvider.Text("done"));
        var (loop, registry, _) = Build(provider);
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake", [StreamJsonRun.PermissionRule.Parse("execute:echo")!]);
        sink.OnEvent = ev => { if (ev.GetProperty("type").GetString() == "permission_request") Answer(stream, ev, "deny"); };

        await loop.RunAsync(Options(stream));

        var req = sink.OfType("permission_request").Single();
        Assert.Equal("b2", req.GetProperty("tool_call_id").GetString());
        Assert.Equal("execute", req.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, req.GetProperty("always").ValueKind); // compound commands cannot be remembered
        var results = sink.OfType("tool_result");
        Assert.True(results.Single(r => r.GetProperty("id").GetString() == "b1").GetProperty("ok").GetBoolean());
        Assert.False(results.Single(r => r.GetProperty("id").GetString() == "b2").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Cancel_DeniesThePendingRequest_AndCancelsTheRun()
    {
        var target = Path.Combine(_dir, "c.txt");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_write", new { path = target, content = "x" }))
            .Then(ScriptedProvider.Text("unreachable"));
        var (loop, registry, _) = Build(provider);
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake");
        using var runCts = new CancellationTokenSource();
        sink.OnEvent = ev =>
        {
            if (ev.GetProperty("type").GetString() == "permission_request")
                stream.HandleControl("{\"type\":\"cancel\"}", runCts);
        };

        try { await loop.RunAsync(Options(stream), runCts.Token); } catch (OperationCanceledException) { }

        Assert.True(runCts.IsCancellationRequested);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ControlChannel_ReadsLines_AndEofDeniesPending()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_write", new { path = Path.Combine(_dir, "eof.txt"), content = "x" }))
            .Then(ScriptedProvider.Text("ok"));
        var (loop, registry, _) = Build(provider);
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake");
        var run = loop.RunAsync(Options(stream));
        // Empty stdin: EOF right away, so the request is denied instead of hanging forever.
        await stream.ReadControlAsync(new StringReader(""), new CancellationTokenSource(), CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(run, finished);
        Assert.False(File.Exists(Path.Combine(_dir, "eof.txt")));
    }

    [Fact]
    public void InvalidControl_EmitsError()
    {
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, new ToolRegistry(), _dir, "m", "fake");
        Assert.False(stream.HandleControl("not json", new CancellationTokenSource()));
        Assert.False(stream.HandleControl("{\"type\":\"permission_response\",\"id\":\"p1\",\"decision\":\"maybe\"}", new CancellationTokenSource()));
        Assert.Equal(2, sink.OfType("error").Count);
    }

    // ── Plan, thinking, modes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TodoTool_EmitsPlanEvents()
    {
        var sessionId = $"plan-{Guid.NewGuid():N}";
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("todo", new { action = "set", tasks = new[] { "Read sample.js", "Fix bug" } }, id: "t1"))
            .Then(ScriptedProvider.Tool("todo", new { action = "update", index = 0, status = "done" }, id: "t2"))
            .Then(ScriptedProvider.Text("done"));
        var (loop, registry, _) = Build(provider);
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake") { CurrentSessionId = sessionId };

        await loop.RunAsync(Options(stream, sessionId: sessionId));

        var plans = sink.OfType("plan");
        Assert.Equal(2, plans.Count);
        var last = plans[^1].GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("text").GetString(), i.GetProperty("status").GetString())).ToList();
        Assert.Equal([("Read sample.js", "done"), ("Fix bug", "pending")], last);
    }

    [Fact]
    public async Task Thinking_IsStreamed()
    {
        var provider = new RoutingProvider(_ => new LlmResponse { Reasoning = "pondering", Text = "answer", StopReason = "end_turn", Usage = new TokenUsage { InputTokens = 5, OutputTokens = 2 } });
        var (loop, registry, _) = Build(provider);
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake");
        await loop.RunAsync(Options(stream));
        Assert.Equal("pondering", sink.OfType("thinking_delta").Single().GetProperty("text").GetString());
    }

    [Fact]
    public async Task PlanMode_NeverAsks_AndNeverWrites()
    {
        var target = Path.Combine(_dir, "plan.txt");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_write", new { path = target, content = "x" }))
            .Then(ScriptedProvider.Text("plan"));
        var (loop, registry, _) = Build(provider);
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake");
        sink.OnEvent = ev => { if (ev.GetProperty("type").GetString() == "permission_request") Answer(stream, ev, "allow"); };
        await loop.RunAsync(Options(stream, mode: "plan"));
        Assert.Empty(sink.OfType("permission_request"));
        Assert.False(File.Exists(target));
    }

    // ── Subagents ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParallelSubagents_StreamNestedEvents_AndAskThroughTheChannel()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0; var maxRunning = 0;
        string FirstUser(CompletionOptions o) => o.Messages.First(m => m.Role == MessageRole.User).GetTextContent();
        var provider = new RoutingProvider(o =>
        {
            var first = FirstUser(o);
            var last = o.Messages[^1];
            if (first.StartsWith("child", StringComparison.Ordinal))
            {
                if (last.Role == MessageRole.User && !last.Content.OfType<ToolResultContent>().Any())
                {
                    // Both children must be in flight at once before either continues.
                    var now = Interlocked.Increment(ref running);
                    lock (gate) maxRunning = Math.Max(maxRunning, now);
                    if (now == 2) gate.TrySetResult();
                    gate.Task.Wait(TimeSpan.FromSeconds(10));
                    return first == "child A"
                        ? ScriptedProvider.Tool("file_write", new { path = Path.Combine(_dir, "child-a.txt"), content = "a" }, id: "w-a")
                        : ScriptedProvider.Text("B done");
                }
                return ScriptedProvider.Text($"{first} done");
            }
            if (last.Content.OfType<ToolResultContent>().Any()) return ScriptedProvider.Text("all done");
            return new LlmResponse
            {
                ToolCalls =
                [
                    new ToolCall { Id = "ag1", Name = "agent", Input = OpenAiProvider.ParseToolArguments(JsonSerializer.Serialize(new { prompt = "child A", description = "Write file A" })) },
                    new ToolCall { Id = "ag2", Name = "agent", Input = OpenAiProvider.ParseToolArguments(JsonSerializer.Serialize(new { prompt = "child B" })) },
                ],
                StopReason = "tool_use",
                Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
            };
        });
        var (loop, registry, router) = Build(provider);
        var logs = Path.Combine(_dir, "logs");
        registry.RegisterTool(new AgentTool(AgentHost.SubagentRunner(new SolvraConfig(), router, registry, new HookEngine(), new AuditLogger(logs), new Tracer(Path.Combine(logs, "t.jsonl"), ObservabilityLevel.Off))));
        var sink = new LineSink();
        var stream = new StreamJsonRun(sink, registry, _dir, "m", "fake");
        sink.OnEvent = ev => { if (ev.GetProperty("type").GetString() == "permission_request") Answer(stream, ev, "allow"); };

        var result = await loop.RunAsync(Options(stream));

        Assert.Equal("all done", result.Text);
        Assert.Equal(2, maxRunning);
        var starts = sink.OfType("agent_start");
        Assert.Equal(2, starts.Count);
        Assert.All(starts, s => Assert.Equal("main", s.GetProperty("parent").GetString()));
        Assert.Contains(starts, s => s.GetProperty("title").GetString() == "Write file A" && s.GetProperty("tool_call_id").GetString() == "ag1");
        var childA = starts.Single(s => s.GetProperty("tool_call_id").GetString() == "ag1").GetProperty("agent").GetString();
        // Ask mode: starting each subagent needs approval in the main agent...
        var requests = sink.OfType("permission_request");
        Assert.Equal(2, requests.Count(r => r.GetProperty("kind").GetString() == "agent" && r.GetProperty("agent").GetString() == "main"));
        // ...and the child's write asked through the same channel under the child's id, then ran.
        Assert.Equal(childA, requests.Single(r => r.GetProperty("tool").GetString() == "file_write").GetProperty("agent").GetString());
        Assert.True(File.Exists(Path.Combine(_dir, "child-a.txt")));
        Assert.Contains(sink.OfType("tool_call"), e => e.GetProperty("agent").GetString() == childA && e.GetProperty("name").GetString() == "file_write");
        Assert.Equal(2, sink.OfType("agent_end").Count(e => e.GetProperty("status").GetString() == "ok"));
        Assert.Contains(sink.OfType("text_delta"), e => e.GetProperty("agent").GetString() != "main");
    }

    [Fact]
    public async Task WithoutObserver_SubagentsStaySequential()
    {
        var running = 0; var maxRunning = 0;
        var provider = new RoutingProvider(o =>
        {
            var first = o.Messages.First(m => m.Role == MessageRole.User).GetTextContent();
            if (first.StartsWith("child", StringComparison.Ordinal))
            {
                var now = Interlocked.Increment(ref running);
                lock (this) maxRunning = Math.Max(maxRunning, now);
                Thread.Sleep(50);
                Interlocked.Decrement(ref running);
                return ScriptedProvider.Text("ok");
            }
            if (o.Messages[^1].Content.OfType<ToolResultContent>().Any()) return ScriptedProvider.Text("all done");
            return new LlmResponse
            {
                ToolCalls =
                [
                    new ToolCall { Id = "s1", Name = "agent", Input = OpenAiProvider.ParseToolArguments("{\"prompt\":\"child 1\"}") },
                    new ToolCall { Id = "s2", Name = "agent", Input = OpenAiProvider.ParseToolArguments("{\"prompt\":\"child 2\"}") },
                ],
                StopReason = "tool_use",
                Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1 }
            };
        });
        var (loop, registry, router) = Build(provider);
        var logs = Path.Combine(_dir, "logs");
        registry.RegisterTool(new AgentTool(AgentHost.SubagentRunner(new SolvraConfig(), router, registry, new HookEngine(), new AuditLogger(logs), new Tracer(Path.Combine(logs, "t.jsonl"), ObservabilityLevel.Off))));
        var result = await loop.RunAsync(new AgentRunOptions
        {
            Prompt = "go",
            Session = new SessionConfig { Id = "seq", CreatedAt = "", Model = "m", Provider = "fake", PermissionMode = "auto", MaxTurns = 5 },
            Cwd = _dir,
            LogToSession = false,
        });
        Assert.Equal("all done", result.Text);
        Assert.Equal(1, maxRunning);
    }

    [Theory]
    [InlineData("OpenAI API error 401: invalid api key", "add_api_key")]
    [InlineData("Missing API key for google", "add_api_key")]
    [InlineData("429 rate limit", "rate_limited")]
    [InlineData("The request timed out", "retry")]
    [InlineData("something odd", null)]
    public void FixFor_MapsErrorsToButtons(string message, string? fix) => Assert.Equal(fix, StreamJsonRun.FixFor(message));
}
