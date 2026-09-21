#nullable enable

using System.Net;
using System.Runtime.CompilerServices;
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

/// <summary>Scripted provider: each call pops the next step (a response or an exception).</summary>
internal sealed class ScriptedProvider : IProvider
{
    private readonly Queue<Func<CompletionOptions, LlmResponse>> _steps = new();
    public List<CompletionOptions> Calls { get; } = new();

    public string Id => "fake";
    public string DisplayName => "Fake";

    public ScriptedProvider Then(LlmResponse r) { _steps.Enqueue(_ => r); return this; }
    public ScriptedProvider Then(Func<CompletionOptions, LlmResponse> f) { _steps.Enqueue(f); return this; }
    public ScriptedProvider Throw(Exception ex) { _steps.Enqueue(_ => throw ex); return this; }

    public Task<LlmResponse> CompleteAsync(CompletionOptions options, CancellationToken ct = default)
    {
        Calls.Add(options);
        if (_steps.Count == 0) throw new InvalidOperationException("script exhausted");
        return Task.FromResult(_steps.Dequeue()(options));
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(CompletionOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await CompleteAsync(options, ct);
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

    public static LlmResponse Text(string text, string stop = "end_turn") =>
        new() { Text = text, StopReason = stop, Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 } };

    public static LlmResponse Tool(string name, object input, string id = "c1") => new()
    {
        ToolCalls = [new ToolCall { Id = id, Name = name, Input = OpenAiProvider.ParseToolArguments(JsonSerializer.Serialize(input)) }],
        StopReason = "tool_use",
        Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
    };

    public static LlmResponse RawTool(string name, string rawArgs, string stop = "tool_use") => new()
    {
        ToolCalls = [new ToolCall { Id = "c1", Name = name, Input = OpenAiProvider.ParseToolArguments(rawArgs) }],
        StopReason = stop,
        Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
    };
}

public class AgentLoopHarnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"solvra-loop-{Guid.NewGuid():N}");

    public AgentLoopHarnessTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AgentLoop Loop(ScriptedProvider provider, ToolRegistry? registry = null)
    {
        var router = new ModelRouter(new Dictionary<string, Func<IProvider>> { ["fake"] = () => provider });
        if (registry == null)
        {
            registry = new ToolRegistry();
            registry.RegisterTool(new FileReadTool());
            registry.RegisterTool(new FileWriteTool());
            registry.RegisterTool(new GlobTool());
        }
        var logs = Path.Combine(_dir, "logs");
        return new AgentLoop(router, registry, new HookEngine(), new AuditLogger(logs),
            costTracker: new CostTracker(Path.Combine(_dir, "ledger.jsonl")),
            sessionManager: new SessionManager(Path.Combine(_dir, "sessions")),
            tracer: new Tracer(Path.Combine(logs, "traces.jsonl"), ObservabilityLevel.Off));
    }

    private AgentRunOptions Options(string prompt, string mode = "auto", int maxTurns = 10) => new()
    {
        Prompt = prompt,
        Session = new SessionConfig
        {
            Id = "t", CreatedAt = DateTime.UtcNow.ToString("o"), Model = "m", Provider = "fake",
            PermissionMode = mode, MaxTurns = maxTurns, MaxBudgetUsd = 100m
        },
        Cwd = _dir,
        LogToSession = false,
    };

    [Fact]
    public async Task ProviderError_ReturnsErrorResult_InsteadOfThrowing()
    {
        var provider = new ScriptedProvider().Throw(new HttpRequestException("OpenAI API error 401: bad key", null, HttpStatusCode.Unauthorized));
        var result = await Loop(provider).RunAsync(Options("hi"));

        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Contains("401", result.Error);
        Assert.Single(provider.Calls); // 401 is not retried
        Assert.Equal(MessageRole.Assistant, result.Messages[^1].Role); // history stays usable
    }

    [Fact]
    public async Task TransientError_IsRetried_OnStreamingPathToo()
    {
        var provider = new ScriptedProvider()
            .Throw(new HttpRequestException("OpenAI API error 500: Internal server error", null, HttpStatusCode.InternalServerError))
            .Then(ScriptedProvider.Text("recovered"));
        var result = await Loop(provider).RunAsync(Options("hi") with { Streaming = true });

        Assert.Equal(StopReason.Text, result.StopReason);
        Assert.Equal("recovered", result.Text);
        Assert.Equal(2, provider.Calls.Count);
    }

    [Fact]
    public async Task QuotaError_IsNotRetried()
    {
        var provider = new ScriptedProvider().Throw(new HttpRequestException("OpenAI API error 429: 5-hour usage limit reached. Resets in 4hr", null, (HttpStatusCode)429));
        var result = await Loop(provider).RunAsync(Options("hi"));
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task InvalidToolArguments_BecomeToolError_AndLoopContinues()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.RawTool("file_read", "{\"path\": \"x", stop: "max_tokens"))
            .Then(o =>
            {
                var tr = o.Messages[^1].Content.OfType<ToolResultContent>().Single();
                Assert.True(tr.IsError);
                Assert.Contains("output token limit", tr.Content);
                return ScriptedProvider.Text("ok");
            });
        var result = await Loop(provider).RunAsync(Options("read"));
        Assert.Equal(StopReason.Text, result.StopReason);
    }

    [Fact]
    public async Task PlanMode_RefusesWrites()
    {
        var target = Path.Combine(_dir, "new.txt");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_write", new { path = target, content = "x" }))
            .Then(o =>
            {
                var tr = o.Messages[^1].Content.OfType<ToolResultContent>().Single();
                Assert.True(tr.IsError);
                Assert.Contains("Plan mode", tr.Content);
                return ScriptedProvider.Text("here is my plan");
            });
        var result = await Loop(provider).RunAsync(Options("change it", mode: "plan"));
        Assert.False(File.Exists(target));
        Assert.Equal("here is my plan", result.Text);
    }

    [Fact]
    public async Task DefaultModeWithoutApprover_RefusesWrites()
    {
        var target = Path.Combine(_dir, "new.txt");
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("file_write", new { path = target, content = "x" }))
            .Then(ScriptedProvider.Text("done"));
        await Loop(provider).RunAsync(Options("write", mode: "default"));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task MaxTokensReply_IsContinued()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Text("first half ", stop: "max_tokens"))
            .Then(ScriptedProvider.Text("second half"));
        var result = await Loop(provider).RunAsync(Options("write a long answer"));
        Assert.Equal("first half second half", result.Text);
    }

    [Fact]
    public async Task SystemPrompt_HasEnvironment_AndProjectInstructions()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "AGENTS.md"), "Always use tabs.");
        var provider = new ScriptedProvider().Then(ScriptedProvider.Text("ok"));
        await Loop(provider).RunAsync(Options("hi"));
        var system = provider.Calls[0].System!;
        Assert.Contains("Working directory: " + _dir, system);
        Assert.Contains("Always use tabs.", system);
        Assert.Contains("coding agent", system);
    }

    [Fact]
    public async Task TurnLimit_IsReportedInText()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Tool("glob", new { pattern = "*.none" }, "a"))
            .Then(ScriptedProvider.Tool("glob", new { pattern = "*.none" }, "b"));
        var result = await Loop(provider).RunAsync(Options("loop", maxTurns: 2));
        Assert.Equal(StopReason.MaxTurns, result.StopReason);
        Assert.Contains("turn limit", result.Text);
    }

    [Fact]
    public async Task ReadOnlyCalls_RunInParallel_AndKeepOrder()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.txt"), "A");
        await File.WriteAllTextAsync(Path.Combine(_dir, "b.txt"), "B");
        var provider = new ScriptedProvider()
            .Then(new LlmResponse
            {
                ToolCalls =
                [
                    new ToolCall { Id = "1", Name = "file_read", Input = OpenAiProvider.ParseToolArguments("{\"path\":\"a.txt\"}") },
                    new ToolCall { Id = "2", Name = "file_read", Input = OpenAiProvider.ParseToolArguments("{\"path\":\"b.txt\"}") },
                ],
                StopReason = "tool_use",
                Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1 }
            })
            .Then(o =>
            {
                var results = o.Messages[^1].Content.OfType<ToolResultContent>().ToList();
                Assert.Equal(["1", "2"], results.Select(r => r.ToolUseId));
                Assert.Contains("A", results[0].Content);
                Assert.Contains("B", results[1].Content);
                return ScriptedProvider.Text("done");
            });
        var result = await Loop(provider).RunAsync(Options("read both"));
        Assert.Equal(StopReason.Text, result.StopReason);
    }
}
