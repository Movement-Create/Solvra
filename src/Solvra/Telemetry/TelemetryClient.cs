using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Solvra.Config;

namespace Solvra.Telemetry;

public record TelemetrySettings(string Endpoint, string DeviceId, string Token, string PublicKey,
    string? LogsDirectory = null, string? SessionsDirectory = null)
{
    public static string ConfigPath => Environment.GetEnvironmentVariable("SOLVRA_TELEMETRY_CONFIG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".solvra", "telemetry.json");
    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Telemetry endpoint must be an HTTPS URL without credentials, query or fragment.");
        if (!Guid.TryParseExact(DeviceId, "N", out _) || Token.Length < 32 || Token.Any(char.IsWhiteSpace))
            throw new ArgumentException("Invalid telemetry device credentials.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKey);
        if (rsa.KeySize < 2048 || !PublicKey.Contains("BEGIN PUBLIC KEY"))
            throw new ArgumentException("Telemetry needs an RSA public key of at least 2048 bits.");
    }
}

public record Cursor(long Offset, string PrefixHash, int PrefixLength);
public record PendingBatch(string Source, Cursor Cursor, JsonElement Body);
public record UploadState(Dictionary<string, Cursor> Cursors, PendingBatch? Pending = null,
    DateTimeOffset? LastDelivered = null, string? LastReceipt = null);

/// <summary>
/// The original JSONL files are the durable queue. Never advances a cursor until the cloud
/// acknowledges the encrypted archive AND metadata delivery. One persisted encrypted batch
/// makes retries stable across process crashes. No network calls on the agent execution path.
/// </summary>
public sealed class TelemetryClient(TelemetrySettings settings, string stateDirectory, HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private const int ChunkSize = 256 * 1024;
    private const int MaxLineBytes = 8 * 1024 * 1024;
    private string StateFile => Path.Combine(stateDirectory, "state.json");

    public static void SavePrivate(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(tmp, options))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(true);
        }
        File.Move(tmp, path, true);
    }

    public UploadState ReadState() => File.Exists(StateFile)
        ? JsonSerializer.Deserialize<UploadState>(File.ReadAllText(StateFile), Json)
            ?? throw new InvalidDataException("Invalid telemetry state; preserve it for recovery.")
        : new(new());

    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        settings.Validate();
        Directory.CreateDirectory(stateDirectory);
        // Works across CLI, VS Code and the scheduled worker. A second sender must not race cursors.
        using var lease = new FileStream(Path.Combine(stateDirectory, "sender.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var state = ReadState();
        int sent = 0;
        if (state.Pending is not null) { state = await SendAsync(state, ct); sent++; }
        var sources = new[] { ("logs", settings.LogsDirectory ?? SolvraPaths.LogsDir),
                              ("sessions", settings.SessionsDirectory ?? SolvraPaths.SessionsDir) };
        foreach (var (kind, directory) in sources)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl").Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var source = kind + "/" + Path.GetFileName(path);
                // Snapshot the size: an active writer cannot make this sync run forever.
                long length = new FileInfo(path).Length;
                while (true)
                {
                    state.Cursors.TryGetValue(source, out var cursor);
                    var chunk = ReadChunk(path, length, cursor);
                    if (chunk is null) break;
                    var (offset, bytes, next) = chunk.Value;
                    var content = new { source, offset, data = Convert.ToBase64String(bytes) };
                    var metadata = Metadata.Extract(bytes, settings.DeviceId);
                    var body = Encrypt(JsonSerializer.SerializeToUtf8Bytes(content, Json), metadata, settings);
                    state = state with { Pending = new(source, next, body) };
                    SavePrivate(StateFile, state); // Must persist before sending anything.
                    state = await SendAsync(state, ct);
                    sent++;
                }
            }
        }
        return sent;
    }

    internal static (long Offset, byte[] Bytes, Cursor Next)? ReadChunk(string path, long limit, Cursor? cursor)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long offset = cursor?.Offset ?? 0;
        if (cursor is not null)
        {
            var prefix = new byte[cursor.PrefixLength];
            var count = file.Read(prefix);
            if (file.Length < offset || count != prefix.Length || Hash(prefix) != cursor.PrefixHash) offset = 0;
        }
        limit = Math.Min(limit, file.Length);
        if (offset >= limit) return null;
        file.Position = offset;
        using var buffer = new MemoryStream();
        var block = new byte[ChunkSize];
        int lastNewline = -1;
        while (buffer.Length < MaxLineBytes && file.Position < limit)
        {
            int count = file.Read(block, 0, (int)Math.Min(block.Length, limit - file.Position));
            if (count == 0) break;
            int last = Array.LastIndexOf(block, (byte)'\n', count - 1, count);
            if (last >= 0) lastNewline = (int)buffer.Length + last;
            buffer.Write(block, 0, count);
            if (lastNewline >= 0) break;
        }
        if (lastNewline < 0)
        {
            if (buffer.Length >= MaxLineBytes) throw new InvalidDataException("Telemetry record exceeds 8 MiB; source retained, upload paused.");
            return null; // Writer has not finished its record yet.
        }
        var bytes = buffer.ToArray()[..(lastNewline + 1)];
        var nextOffset = offset + bytes.Length;
        var fingerprint = new byte[(int)Math.Min(256, nextOffset)];
        file.Position = 0;
        file.ReadExactly(fingerprint);
        return (offset, bytes, new(nextOffset, Hash(fingerprint), fingerprint.Length));
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static JsonElement Encrypt(byte[] plaintext, object metadata, TelemetrySettings settings)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(settings.DeviceId));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(settings.PublicKey);
        var wrapped = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
        CryptographicOperations.ZeroMemory(key);
        return JsonSerializer.SerializeToElement(new
        {
            version = 1, device_id = settings.DeviceId, id = Hash(cipher),
            envelope = new { key = Convert.ToBase64String(wrapped), nonce = Convert.ToBase64String(nonce),
                ciphertext = Convert.ToBase64String(cipher), tag = Convert.ToBase64String(tag) }, metadata
        }, Json);
    }

    private async Task<UploadState> SendAsync(UploadState state, CancellationToken ct)
    {
        var pending = state.Pending!;
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint.TrimEnd('/') + "/v1/batches");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        request.Content = new StringContent(pending.Body.GetRawText(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Telemetry collector returned HTTP {(int)response.StatusCode}; queued records retained.");
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var id = pending.Body.GetProperty("id").GetString();
        if (receipt.RootElement.GetProperty("id").GetString() != id ||
            !receipt.RootElement.GetProperty("archived").GetBoolean() ||
            !receipt.RootElement.GetProperty("metadata_delivered").GetBoolean())
            throw new IOException("Collector did not confirm delivery; queued records retained.");
        state.Cursors[pending.Source] = pending.Cursor;
        state = state with { Pending = null, LastDelivered = DateTimeOffset.UtcNow, LastReceipt = id };
        SavePrivate(StateFile, state);
        return state;
    }
}
