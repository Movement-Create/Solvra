#nullable enable

using Solvra.Providers;
using Xunit;

namespace Solvra.Tests.Harness;

public class ChatGptParserTests
{
    [Fact]
    public void ToolCallStream_ProducesStartDeltaEnd_AndToolUseStop()
    {
        var p = new ResponsesStreamParser();
        var events = new List<StreamEvent>();
        events.AddRange(p.Feed("""{"type":"response.output_text.delta","delta":"Looking."}"""));
        events.AddRange(p.Feed("""{"type":"response.output_item.added","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"bash"}}"""));
        events.AddRange(p.Feed("""{"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"command\":\"ls\"}"}"""));
        events.AddRange(p.Feed("""{"type":"response.output_item.done","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"bash","arguments":"{\"command\":\"ls\"}"}}"""));
        events.AddRange(p.Feed("""{"type":"response.completed","response":{"usage":{"input_tokens":12,"output_tokens":3}}}"""));

        Assert.Equal("Looking.", events.OfType<StreamText>().Single().Delta);
        Assert.Equal(("call_1", "bash"), events.OfType<StreamToolUseStart>().Select(s => (s.Id, s.Name)).Single());
        Assert.Equal("{\"command\":\"ls\"}", string.Concat(events.OfType<StreamToolUseDelta>().Select(d => d.JsonFragment))); // not duplicated
        Assert.Single(events.OfType<StreamToolUseEnd>());
        var end = events.OfType<StreamMessageEnd>().Single();
        Assert.Equal("tool_use", end.StopReason);
        Assert.Equal(12, end.Usage.InputTokens);
        Assert.True(p.Finished);
    }

    [Fact]
    public void IncompleteResponse_MapsToMaxTokens_AndFailedThrows()
    {
        var p = new ResponsesStreamParser();
        var end = p.Feed("""{"type":"response.incomplete","response":{}}""").OfType<StreamMessageEnd>().Single();
        Assert.Equal("max_tokens", end.StopReason);

        var failed = new ResponsesStreamParser();
        var ex = Assert.Throws<HttpRequestException>(() => failed.Feed("""{"type":"response.failed","response":{"error":{"message":"quota"}}}""").ToList());
        Assert.Contains("quota", ex.Message);
    }
}
