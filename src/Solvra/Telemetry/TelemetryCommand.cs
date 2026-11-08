using System.CommandLine;
using System.Text.Json;
using Solvra.Config;

namespace Solvra.Telemetry;

public static class TelemetryCommand
{
    private static void WriteProbe(TelemetrySettings settings)
    {
        var id = Guid.NewGuid().ToString("N");
        var trace = id[..16];
        var now = DateTimeOffset.UtcNow.ToString("O");
        var dir = settings.SessionsDirectory ?? SolvraPaths.SessionsDir;
        Directory.CreateDirectory(dir);
        var events = new object[]
        {
            new { type="run_start", timestamp=now, data=new { trace_id=trace, agent="solvra", purpose="telemetry-connectivity-test" } },
            new { type="assistant_turn", timestamp=now, data=new { content=new[] { new { type="tool_use", id="check-"+id, name="decide", input=new { question="Synthetic telemetry delivery check", decision="verify collector receipt" } } } } },
            new { type="tool_result", timestamp=now, data=new { call_id="check-"+id, output="Synthetic check", is_error=false } },
            new { type="run_end", timestamp=now, data=new { trace_id=trace, decisions=1, stop_reason="telemetry_test" } }
        };
        File.WriteAllLines(Path.Combine(dir,"telemetry-check-"+id+".jsonl"), events.Select(e=>JsonSerializer.Serialize(e,TelemetryClient.Json)));
        dir=settings.LogsDirectory ?? SolvraPaths.LogsDir;
        Directory.CreateDirectory(dir);
        var span=new { type="span_end", timestamp=now, start_time=now, trace_id=trace, span_id=id[16..], name="agent.session", duration_ms=0 };
        File.AppendAllText(Path.Combine(dir,"telemetry-checks.jsonl"),JsonSerializer.Serialize(span,TelemetryClient.Json)+"\n");
        Console.WriteLine("Created synthetic delivery check: " + trace);
    }

    public static Command Build()
    {
        var root = new Command("telemetry", "Configure and deliver encrypted telemetry to your collector");
        var configure = new Command("configure", "Import a private device setup file (never put it in a repository)");
        var file = new Option<string>("--file") { IsRequired = true };
        configure.AddOption(file);
        configure.SetHandler(path =>
        {
            var config = JsonSerializer.Deserialize<TelemetrySettings>(File.ReadAllText(path), TelemetryClient.Json)
                ?? throw new ArgumentException("Invalid device setup file.");
            config.Validate();
            var current = TelemetrySettings.ConfigPath;
            if (File.Exists(current))
            {
                var prior = JsonSerializer.Deserialize<TelemetrySettings>(File.ReadAllText(current), TelemetryClient.Json)!;
                if (prior.DeviceId != config.DeviceId)
                    throw new InvalidOperationException("A different device is already configured. Preserve its queue before switching identity.");
            }
            TelemetryClient.SavePrivate(current, config);
            Console.WriteLine("Telemetry configured. Run 'solvra telemetry sync' to verify delivery.");
        }, file);
        root.AddCommand(configure);
        foreach (var mode in new[] { "sync", "watch", "status", "test" })
        {
            var cmd = new Command(mode, mode == "watch" ? "Retry queued records every 60 seconds" : mode == "sync" ? "Upload pending records once" : "Show delivery state without credentials");
            cmd.SetHandler(async context =>
            {
                var ct = context.GetCancellationToken();
                if (!File.Exists(TelemetrySettings.ConfigPath))
                {
                    Console.WriteLine("Telemetry is not configured. Import your private device setup file first.");
                    context.ExitCode = 1; return;
                }
                var settings = JsonSerializer.Deserialize<TelemetrySettings>(await File.ReadAllTextAsync(TelemetrySettings.ConfigPath, ct), TelemetryClient.Json)!;
                settings.Validate();
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
                var stateDir = Path.Combine(Path.GetDirectoryName(TelemetrySettings.ConfigPath)!, "telemetry-state", settings.DeviceId);
                var client = new TelemetryClient(settings, stateDir, http);
                if (mode == "status")
                {
                    var s = client.ReadState();
                    Console.WriteLine(JsonSerializer.Serialize(new { configured = true, device_id = settings.DeviceId,
                        last_delivered = s.LastDelivered, last_receipt = s.LastReceipt, retry_pending = s.Pending is not null,
                        tracked_files = s.Cursors.Count, sender = File.Exists(Path.Combine(stateDir,"health.json"))
                            ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(stateDir,"health.json"))) : (JsonElement?)null }, TelemetryClient.Json));
                    return;
                }
                if (mode == "test") WriteProbe(settings);
                do
                {
                    try
                    {
                        var count = await client.SyncAsync(ct);
                        TelemetryClient.SavePrivate(Path.Combine(stateDir, "health.json"), new { last_attempt = DateTimeOffset.UtcNow, error = (string?)null });
                        if (count > 0 || mode != "watch") Console.WriteLine($"{DateTimeOffset.UtcNow:O} telemetry: {count} batches delivered.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Never echo remote bodies, paths or credentials from an exception.
                        TelemetryClient.SavePrivate(Path.Combine(stateDir, "health.json"), new { last_attempt = DateTimeOffset.UtcNow, error = ex.GetType().Name });
                        Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} telemetry: delivery failed ({ex.GetType().Name}); queued records retained. Check connectivity and device credentials.");
                        if (mode is "sync" or "test") { context.ExitCode = 1; return; }
                    }
                    if (mode == "watch") await Task.Delay(TimeSpan.FromSeconds(60), ct);
                } while (mode == "watch" && !ct.IsCancellationRequested);
            });
            root.AddCommand(cmd);
        }
        return root;
    }
}
