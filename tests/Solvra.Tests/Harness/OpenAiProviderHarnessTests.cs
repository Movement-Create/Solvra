#nullable enable

using System.Net;
using System.Text;
using System.Text.Json;
using Solvra.Models;
using Solvra.Providers;
using Xunit;

namespace Solvra.Tests.Harness;

public class OpenAiProviderHarnessTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request, LastBody ?? "");
        }
    }

    private static HttpResponseMessage Sse(params string[] chunks)
    {
        var body = string.Join("", chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }

    private static CompletionOptions Options(params Message[] messages) => new()
    {
        Model = "qwen3.8-flash",
        Messages = messages.Length > 0 ? messages : [Message.FromText(MessageRole.User, "hi")],
    };

    private static async Task<List<StreamEvent>> Collect(OpenAiProvider p, CompletionOptions o)
    {
        var list = new List<StreamEvent>();
        await foreach (var e in p.StreamAsync(o)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Stream_RepeatedIdOnEveryChunk_KeepsAllArguments()
    {
        // DashScope-style: id and name repeated on each chunk; the old parser reset the buffer.
        var handler = new FakeHandler((_, _) => Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"bash","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"bash","arguments":"{\"command\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"bash","arguments":"\"ls -la\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":11,"completion_tokens":7}}"""));
        var p = new OpenAiProvider(new HttpClient(handler), "k", "http://x/v1");

        var events = await Collect(p, Options());

        var starts = events.OfType<StreamToolUseStart>().ToList();
        Assert.Single(starts);
        Assert.Equal("bash", starts[0].Name);
        var args = string.Concat(events.OfType<StreamToolUseDelta>().Select(d => d.JsonFragment));
        Assert.Equal("ls -la", OpenAiProvider.ParseToolArguments(args)["command"].GetString());
        var end = events.OfType<StreamMessageEnd>().Single();
        Assert.Equal("tool_use", end.StopReason);
        Assert.Equal(11, end.Usage.InputTokens); // usage chunk after finish_reason is kept
    }

    [Fact]
    public async Task Stream_BlankIdOnLaterChunks_AndMissingIndex()
    {
        var handler = new FakeHandler((_, _) => Sse(
            """{"choices":[{"delta":{"tool_calls":[{"id":"a","function":{"name":"file_read","arguments":"{\"path\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"id":"","function":{"arguments":"\"x.txt\"}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"id":"b","function":{"name":"glob","arguments":"{\"pattern\":\"*.cs\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"""));
        var p = new OpenAiProvider(new HttpClient(handler), "k", "http://x/v1");

        var events = await Collect(p, Options());
        var starts = events.OfType<StreamToolUseStart>().Select(s => (s.Id, s.Name)).ToList();
        Assert.Equal([("a", "file_read"), ("b", "glob")], starts);
        var argsA = string.Concat(events.OfType<StreamToolUseDelta>().Where(d => d.Id == "a").Select(d => d.JsonFragment));
        Assert.Equal("x.txt", OpenAiProvider.ParseToolArguments(argsA)["path"].GetString());
    }

    [Fact]
    public async Task Stream_ReasoningIsEmitted_AndLengthMapsToMaxTokens()
    {
        var handler = new FakeHandler((_, _) => Sse(
            """{"choices":[{"delta":{"reasoning_content":"thinking..."}}]}""",
            """{"choices":[{"delta":{"content":"Hello"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"length"}]}"""));
        var p = new OpenAiProvider(new HttpClient(handler), "k", "http://x/v1");

        var events = await Collect(p, Options());
        Assert.Equal("thinking...", events.OfType<StreamReasoning>().Single().Delta);
        Assert.Equal("max_tokens", events.OfType<StreamMessageEnd>().Single().StopReason);
    }

    [Fact]
    public async Task Request_ReplaysReasoningContent_AndAsksForStreamUsage()
    {
        var handler = new FakeHandler((_, _) => Sse("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var p = new OpenAiProvider(new HttpClient(handler), "k", "http://x/v1");
        var assistant = new Message
        {
            Role = MessageRole.Assistant,
            Content = [new ReasoningContent { Text = "plan" }, new ToolUseContent { Id = "c1", Name = "bash", Input = new() }]
        };
        var tool = new Message { Role = MessageRole.Tool, Content = [new ToolResultContent { ToolUseId = "c1", Content = "out" }] };

        await Collect(p, Options(Message.FromText(MessageRole.User, "hi"), assistant, tool));

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var msgs = doc.RootElement.GetProperty("messages");
        var asst = msgs.EnumerateArray().First(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal("plan", asst.GetProperty("reasoning_content").GetString());
        Assert.Equal(JsonValueKind.String, asst.GetProperty("content").ValueKind);
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public void ParseResponse_NullToolCalls_DoesNotThrow()
    {
        var r = OpenAiProvider.ParseResponse("""{"choices":[{"message":{"role":"assistant","content":"done","tool_calls":null},"finish_reason":"stop"}]}""");
        Assert.Equal("done", r.Text);
        Assert.Empty(r.ToolCalls);
        Assert.Equal("end_turn", r.StopReason);
        Assert.Equal(0, r.Usage.InputTokens); // missing usage tolerated
    }

    [Fact]
    public void ParseToolArguments_MarksInvalidJson()
    {
        var parsed = OpenAiProvider.ParseToolArguments("{\"command\": \"ls");
        Assert.True(parsed.ContainsKey(OpenAiProvider.ArgumentParseErrorKey));
        Assert.Empty(OpenAiProvider.ParseToolArguments(""));
    }

    [Fact]
    public async Task HttpError_CarriesProviderMessage_AndRetryAfter()
    {
        var handler = new FakeHandler((_, _) =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("""{"error":{"message":"slow down"}}""") };
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return resp;
        });
        var p = new OpenAiProvider(new HttpClient(handler), "k", "http://x/v1");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.CompleteAsync(Options()));
        Assert.Contains("slow down", ex.Message);
        Assert.Equal(7, ex.Data["RetryAfterSeconds"]);
    }

    [Fact]
    public void UnknownModel_CostsNothing()
    {
        var p = new OpenAiProvider(new HttpClient(), "k", "http://x/v1");
        Assert.Equal(0m, p.EstimateCost("glm-5.3-flash", 1_000_000, 1_000_000));
        Assert.True(p.EstimateCost("gpt-4o", 1_000_000, 0) > 0);
    }
}
