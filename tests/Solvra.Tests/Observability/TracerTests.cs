#nullable enable

using Solvra.Observability;
using Xunit;

namespace Solvra.Tests.Observability;

public class TracerTests
{
    private string CreateTempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tracer_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.spans.jsonl");
    }

    [Fact]
    public void StartSpan_CreatesSpanWithCorrectName()
    {
        var path = CreateTempPath();
        var tracer = new Tracer(Path.GetDirectoryName(path)!, "test-session", ObservabilityLevel.Normal);

        using (var span = tracer.StartSpan("agent.session"))
        {
            Assert.NotNull(tracer.CurrentSpan);
            Assert.Equal("agent.session", tracer.CurrentSpan!.Name);
        }
    }

    [Fact]
    public void StartSpan_DisposingClearsCurrentSpan()
    {
        var path = CreateTempPath();
        var tracer = new Tracer(Path.GetDirectoryName(path)!, "test-session", ObservabilityLevel.Normal);

        using (tracer.StartSpan("agent.session"))
        {
            Assert.NotNull(tracer.CurrentSpan);
        }

        Assert.Null(tracer.CurrentSpan);
    }

    [Fact]
    public void NestedSpans_PropagateParentId()
    {
        var path = CreateTempPath();
        var tracer = new Tracer(Path.GetDirectoryName(path)!, "test-session", ObservabilityLevel.Normal);

        using (var outerSpan = tracer.StartSpan("agent.session"))
        {
            var outerContext = tracer.CurrentSpan!;
            Assert.Null(outerContext.ParentSpanId);

            using (var innerSpan = tracer.StartSpan("agent.turn"))
            {
                var innerContext = tracer.CurrentSpan!;
                Assert.Equal("agent.turn", innerContext.Name);
                Assert.Equal(outerContext.SpanId, innerContext.ParentSpanId);
                Assert.Equal(outerContext.TraceId, innerContext.TraceId);
            }

            // After inner dispose, current should be back to outer
            Assert.Equal(outerContext.SpanId, tracer.CurrentSpan!.SpanId);
        }
    }

    [Fact]
    public void TripleNestedSpans_MaintainCorrectParentChain()
    {
        var path = CreateTempPath();
        var tracer = new Tracer(Path.GetDirectoryName(path)!, "test-session", ObservabilityLevel.Normal);

        using (tracer.StartSpan("agent.session"))
        {
            var sessionSpan = tracer.CurrentSpan!;

            using (tracer.StartSpan("agent.turn"))
            {
                var turnSpan = tracer.CurrentSpan!;
                Assert.Equal(sessionSpan.SpanId, turnSpan.ParentSpanId);

                using (tracer.StartSpan("tool.execute"))
                {
                    var toolSpan = tracer.CurrentSpan!;
                    Assert.Equal(turnSpan.SpanId, toolSpan.ParentSpanId);
                    Assert.Equal(sessionSpan.TraceId, toolSpan.TraceId);
                }

                Assert.Equal(turnSpan.SpanId, tracer.CurrentSpan!.SpanId);
            }

            Assert.Equal(sessionSpan.SpanId, tracer.CurrentSpan!.SpanId);
        }
    }

    [Fact]
    public void WritesJsonlEvents()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tracer_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tracer = new Tracer(dir, "test-session", ObservabilityLevel.Normal);
            var spanFile = Path.Combine(dir, "test-session.spans.jsonl");

            using (tracer.StartSpan("agent.session"))
            {
                using (tracer.StartSpan("agent.turn"))
                {
                    // span start/end for turn
                }
            }

            Assert.True(File.Exists(spanFile));
            var lines = File.ReadAllLines(spanFile);
            // 2 spans x 2 events (start + end) = 4 lines
            Assert.Equal(4, lines.Length);
            Assert.Contains("span_start", lines[0]);
            Assert.Contains("span_end", lines[^1]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void OnSpanEvent_FiresOnStartAndEnd()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tracer_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tracer = new Tracer(dir, "test-session", ObservabilityLevel.Normal);
            var events = new List<SpanEvent>();
            tracer.OnSpanEvent += events.Add;

            using (tracer.StartSpan("agent.session"))
            {
                // span_start event should have fired
            }

            Assert.Equal(2, events.Count);
            Assert.Equal("span_start", events[0].EventType);
            Assert.Equal("span_end", events[1].EventType);
            Assert.NotNull(events[1].DurationMs);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StartSpan_WithAttributes_StoresAttributes()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tracer_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tracer = new Tracer(dir, "test-session", ObservabilityLevel.Normal);
            var attrs = new Dictionary<string, object> { ["tool_name"] = "bash" };

            using (tracer.StartSpan("tool.execute", attrs))
            {
                Assert.Equal("bash", tracer.CurrentSpan!.Attributes!["tool_name"]);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Off_Level_DisablesAllSpans()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tracer_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tracer = new Tracer(dir, "test-session", ObservabilityLevel.Off);
            var events = new List<SpanEvent>();
            tracer.OnSpanEvent += events.Add;

            using (tracer.StartSpan("agent.session")) { }

            Assert.Empty(events);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AddEvent_RecordsSpanEvent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tracer_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tracer = new Tracer(dir, "test-session", ObservabilityLevel.Normal);
            var events = new List<SpanEvent>();
            tracer.OnSpanEvent += events.Add;

            using (tracer.StartSpan("agent.session"))
            {
                tracer.AddEvent("checkpoint", new Dictionary<string, object> { ["step"] = 1 });
            }

            // span_start + span_event + span_end = 3
            Assert.Equal(3, events.Count);
            Assert.Equal("span_event", events[1].EventType);
            Assert.Equal("checkpoint", events[1].EventName);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
