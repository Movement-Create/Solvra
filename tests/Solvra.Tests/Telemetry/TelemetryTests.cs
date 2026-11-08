using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Solvra.Telemetry;

namespace Solvra.Tests.Telemetry;

public class TelemetryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "solvra-telemetry-" + Guid.NewGuid().ToString("N"));
    private readonly RSA rsa = RSA.Create(2048);
    public TelemetryTests() { Directory.CreateDirectory(Path.Combine(root, "logs")); }
    private TelemetrySettings Settings => new("https://collector.example", new string('a',32), new string('b',48), rsa.ExportSubjectPublicKeyInfoPem(), Path.Combine(root,"logs"), Path.Combine(root,"sessions"));
    private string State => Path.Combine(root, "state");
    private string Source => Path.Combine(root, "logs", "audit.jsonl");
    private const string Record = "{\"event_type\":\"model_call_completed\",\"timestamp\":\"2026-11-08T00:00:00Z\",\"data\":{\"input_tokens\":12,\"prompt\":\"private-customer-code\"}}\n";

    [Fact]
    public async Task RetryAfterRestartUsesIdenticalEncryptedBatchAndDoesNotAdvanceEarly()
    {
        File.WriteAllText(Source, Record);
        string? first = null;
        using (var http = new HttpClient(new Handler(request => { first = request; return new(HttpStatusCode.ServiceUnavailable); })))
        {
            var client = new TelemetryClient(Settings, State, http);
            await Assert.ThrowsAsync<IOException>(() => client.SyncAsync());
            Assert.NotNull(client.ReadState().Pending);
            Assert.Empty(client.ReadState().Cursors);
        }
        Assert.DoesNotContain("private-customer-code", first!);
        File.AppendAllText(Source, Record); // Captured after the retried batch, not lost.
        var bodies = new List<string>();
        using var healthy = new HttpClient(new Handler(request => { bodies.Add(request); return Ack(request); }));
        var resumed = new TelemetryClient(Settings, State, healthy);
        Assert.Equal(2, await resumed.SyncAsync());
        Assert.Equal(first, bodies[0]);
        Assert.Equal(Encoding.UTF8.GetByteCount(Record)*2, resumed.ReadState().Cursors["logs/audit.jsonl"].Offset);
        Assert.Equal(0, await resumed.SyncAsync());
        Assert.Null(resumed.ReadState().Pending);
    }

    [Fact]
    public void EncryptionRoundTripsAndAuthenticatesDevice()
    {
        var body = TelemetryClient.Encrypt(Encoding.UTF8.GetBytes(Record), Array.Empty<object>(), Settings);
        var e = body.GetProperty("envelope");
        byte[] Bytes(string key) => Convert.FromBase64String(e.GetProperty(key).GetString()!);
        var key = rsa.Decrypt(Bytes("key"), RSAEncryptionPadding.OaepSHA256);
        var plain = new byte[Bytes("ciphertext").Length];
        using var aes = new AesGcm(key,16);
        aes.Decrypt(Bytes("nonce"), Bytes("ciphertext"), Bytes("tag"), plain, Encoding.UTF8.GetBytes(Settings.DeviceId));
        Assert.Equal(Record, Encoding.UTF8.GetString(plain));
        Assert.Throws<AuthenticationTagMismatchException>(() => aes.Decrypt(Bytes("nonce"), Bytes("ciphertext"), Bytes("tag"), plain, "wrong-device"u8));
    }

    [Fact]
    public async Task PartialLineIsRetainedUntilWriterFinishes()
    {
        File.WriteAllText(Source, Record.TrimEnd('\n'));
        using var http = new HttpClient(new Handler(Ack));
        var client = new TelemetryClient(Settings, State, http);
        Assert.Equal(0, await client.SyncAsync());
        File.AppendAllText(Source,"\n");
        Assert.Equal(1, await client.SyncAsync());
    }

    [Fact]
    public async Task TruncationRestartsAtBeginning()
    {
        File.WriteAllText(Source,Record+Record);
        using var http = new HttpClient(new Handler(Ack));
        var client = new TelemetryClient(Settings, State, http);
        await client.SyncAsync();
        File.WriteAllText(Source,Record);
        Assert.Equal(1, await client.SyncAsync());
        Assert.Equal(Encoding.UTF8.GetByteCount(Record),client.ReadState().Cursors["logs/audit.jsonl"].Offset);
    }

    [Fact]
    public async Task IncorrectReceiptCannotDiscardQueuedRecords()
    {
        File.WriteAllText(Source,Record);
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"wrong\",\"archived\":true,\"metadata_delivered\":true}") }));
        var client = new TelemetryClient(Settings, State, http);
        await Assert.ThrowsAsync<IOException>(() => client.SyncAsync());
        Assert.Empty(client.ReadState().Cursors);
    }

    [Fact]
    public void MetadataOnlyContainsAllowedNumbersAndFixedNames()
    {
        var json = JsonSerializer.Serialize(Metadata.Extract(Encoding.UTF8.GetBytes(Record),Settings.DeviceId));
        Assert.Contains("input_tokens",json);
        Assert.DoesNotContain("private-customer-code",json);
        Assert.DoesNotContain("prompt",json);
    }

    [Fact]
    public void RejectsInsecureOrCredentialBearingEndpoints()
    {
        foreach(var endpoint in new[]{"http://example.com", "https://user:pass@example.com", "https://example.com?token=x"})
            Assert.Throws<ArgumentException>(() => (Settings with { Endpoint = endpoint }).Validate());
    }

    private static HttpResponseMessage Ack(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id=doc.RootElement.GetProperty("id").GetString(), archived=true,metadata_delivered=true })) };
    }
    private sealed class Handler(Func<string,HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => reply(await request.Content!.ReadAsStringAsync(ct));
    }
    public void Dispose() { rsa.Dispose(); Directory.Delete(root,true); }
}
