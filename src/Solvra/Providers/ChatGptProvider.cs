using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solvra.Models;

namespace Solvra.Providers;

/// <summary>
/// Uses the ChatGPT subscription that the Codex CLI is signed in with ("chatgpt:&lt;model&gt;").
/// Codex stays the only owner of its login: Solvra reads ~/.codex/auth.json (or $CODEX_HOME)
/// and, when the access token is about to expire or is rejected, asks `codex app-server`
/// to refresh it (account/read refreshToken=true), then reads the file again. Solvra never
/// writes Codex's credentials, so the two can never race on a rotated refresh token.
/// Requests go to the Responses API behind chatgpt.com/backend-api/codex.
/// </summary>
public sealed class ChatGptProvider : IProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ICodexAuthSource _auth;
    private readonly string _sessionId = Guid.NewGuid().ToString();

    public string Id => "chatgpt";
    public string DisplayName => "ChatGPT (Codex sign-in)";

    public ChatGptProvider(HttpClient? http = null, ICodexAuthSource? auth = null, string? baseUrl = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _auth = auth ?? new CodexAuthSource();
        _baseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("SOLVRA_CHATGPT_BASE_URL") ?? "https://chatgpt.com/backend-api/codex").TrimEnd('/');
    }

    public async Task<LlmResponse> CompleteAsync(CompletionOptions options, CancellationToken ct = default)
    {
        // The backend only streams; collect the stream into one response.
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        var args = new Dictionary<string, StringBuilder>();
        TokenUsage usage = new();
        var stop = "end_turn";
        await foreach (var ev in StreamAsync(options, ct))
        {
            switch (ev)
            {
                case StreamText t: text.Append(t.Delta); break;
                case StreamToolUseStart s: args[s.Id] = new StringBuilder(); calls.Add(new ToolCall { Id = s.Id, Name = s.Name, Input = new Dictionary<string, JsonElement>() }); break;
                case StreamToolUseDelta d: if (args.TryGetValue(d.Id, out var sb)) sb.Append(d.JsonFragment); break;
                case StreamToolUseEnd e: { var c = calls.FirstOrDefault(x => x.Id == e.Id); if (c != null && args.TryGetValue(e.Id, out var a)) c.Input = OpenAiProvider.ParseToolArguments(a.ToString()); break; }
                case StreamMessageEnd m: usage = m.Usage; stop = m.StopReason; break;
            }
        }
        return new LlmResponse { Text = text.ToString(), ToolCalls = calls, StopReason = stop, Usage = usage };
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(CompletionOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequest(options).ToJsonString();
        var creds = await _auth.GetAsync(forceRefresh: false, ct);
        var response = await SendAsync(body, creds, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            creds = await _auth.GetAsync(forceRefresh: true, ct);
            response = await SendAsync(body, creds, ct);
        }
        using (response)
        {
            await HttpErrors.EnsureSuccessAsync(response, "ChatGPT", ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var parser = new ResponsesStreamParser();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data.Length == 0 || data == "[DONE]") continue;
                foreach (var ev in parser.Feed(data)) yield return ev;
                if (parser.Finished) break;
            }
            foreach (var ev in parser.Finish()) yield return ev;
        }
    }

    private Task<HttpResponseMessage> SendAsync(string body, CodexCredentials creds, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/responses")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        if (!string.IsNullOrEmpty(creds.AccountId)) req.Headers.Add("chatgpt-account-id", creds.AccountId);
        req.Headers.Add("OpenAI-Beta", "responses=experimental");
        req.Headers.Add("originator", Environment.GetEnvironmentVariable("SOLVRA_CHATGPT_ORIGINATOR") ?? "solvra");
        req.Headers.Add("session_id", _sessionId);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Solvra messages → Responses API request (stateless: store=false, full history each turn).</summary>
    internal static JsonObject BuildRequest(CompletionOptions options)
    {
        var input = new JsonArray();
        foreach (var msg in options.Messages)
        {
            switch (msg.Role)
            {
                case MessageRole.System:
                    continue;
                case MessageRole.Tool:
                    foreach (var tr in msg.Content.OfType<ToolResultContent>())
                        input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = tr.ToolUseId, ["output"] = tr.Content ?? "" });
                    continue;
                case MessageRole.Assistant:
                {
                    var text = string.Join("", msg.Content.OfType<TextContent>().Select(t => t.Text));
                    if (!string.IsNullOrEmpty(text))
                        input.Add(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) });
                    foreach (var tu in msg.Content.OfType<ToolUseContent>())
                        input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = tu.Id, ["name"] = tu.Name, ["arguments"] = JsonSerializer.Serialize(tu.Input) });
                    continue;
                }
                default:
                {
                    var parts = new JsonArray();
                    foreach (var part in msg.Content)
                    {
                        if (part is TextContent t && !string.IsNullOrEmpty(t.Text)) parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = t.Text });
                        else if (part is ImageContent ic)
                            parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = ic.Source.Url ?? $"data:{ic.Source.MediaType};base64,{ic.Source.Data}" });
                        else if (part is ToolResultContent tr)
                            input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = tr.ToolUseId, ["output"] = tr.Content ?? "" });
                    }
                    if (parts.Count > 0) input.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = parts });
                    continue;
                }
            }
        }

        var request = new JsonObject
        {
            ["model"] = options.Model,
            ["instructions"] = string.IsNullOrWhiteSpace(options.System) ? "You are Solvra, a capable software engineering agent." : options.System,
            ["input"] = input,
            ["store"] = false,
            ["stream"] = true,
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = true,
        };
        if (options.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in options.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(JsonSerializer.Serialize(tool.InputSchema)),
                    ["strict"] = false,
                });
            }
            request["tools"] = tools;
        }
        return request;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var server = await CodexAppServer.StartAsync(ct);
        var result = await server.RequestAsync("model/list", new JsonObject { ["includeHidden"] = false }, ct);
        var models = new List<string>();
        if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("model", out var mm) && mm.ValueKind == JsonValueKind.String ? mm.GetString()
                    : m.TryGetProperty("id", out var mi) && mi.ValueKind == JsonValueKind.String ? mi.GetString() : null;
                if (!string.IsNullOrEmpty(id) && !models.Contains(id)) models.Add(id);
            }
        }
        return models;
    }

    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        try { await _auth.GetAsync(false, ct); return true; }
        catch { return false; }
    }

    /// <summary>Covered by the ChatGPT subscription; there is no per-token charge to track.</summary>
    public decimal EstimateCost(string model, int inputTokens, int outputTokens) => 0m;
}

/// <summary>Incremental parser for Responses API server-sent events.</summary>
internal sealed class ResponsesStreamParser
{
    private readonly Dictionary<string, string> _itemToCall = new();   // output item id → call_id
    private readonly HashSet<string> _argsStreamed = new();
    private readonly HashSet<string> _ended = new();
    private bool _sawTool;
    private bool _endEmitted;
    public bool Finished { get; private set; }

    public IEnumerable<StreamEvent> Feed(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        var events = new List<StreamEvent>();
        switch (type)
        {
            case "response.output_text.delta":
                if (root.TryGetProperty("delta", out var d) && d.GetString() is { Length: > 0 } text) events.Add(new StreamText(text));
                break;
            case "response.output_item.added":
                if (root.TryGetProperty("item", out var added) && Str(added, "type") == "function_call")
                {
                    var callId = Str(added, "call_id") ?? Str(added, "id") ?? Guid.NewGuid().ToString();
                    if (Str(added, "id") is { } itemId) _itemToCall[itemId] = callId;
                    _sawTool = true;
                    events.Add(new StreamToolUseStart(callId, Str(added, "name") ?? ""));
                }
                break;
            case "response.function_call_arguments.delta":
            {
                var itemId = Str(root, "item_id");
                if (itemId != null && _itemToCall.TryGetValue(itemId, out var callId) && Str(root, "delta") is { Length: > 0 } frag)
                {
                    _argsStreamed.Add(callId);
                    events.Add(new StreamToolUseDelta(callId, frag));
                }
                break;
            }
            case "response.output_item.done":
                if (root.TryGetProperty("item", out var item) && Str(item, "type") == "function_call")
                {
                    var callId = Str(item, "call_id") ?? (Str(item, "id") is { } iid && _itemToCall.TryGetValue(iid, out var c) ? c : null);
                    if (callId == null) break;
                    if (!_itemToCall.ContainsValue(callId)) { _sawTool = true; events.Add(new StreamToolUseStart(callId, Str(item, "name") ?? "")); }
                    if (!_argsStreamed.Contains(callId) && Str(item, "arguments") is { Length: > 0 } all) events.Add(new StreamToolUseDelta(callId, all));
                    if (_ended.Add(callId)) events.Add(new StreamToolUseEnd(callId));
                }
                break;
            case "response.completed":
            case "response.incomplete":
            {
                var usage = new TokenUsage();
                if (root.TryGetProperty("response", out var resp) && resp.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    usage = new TokenUsage { InputTokens = Int(u, "input_tokens"), OutputTokens = Int(u, "output_tokens") };
                foreach (var callId in _itemToCall.Values.Where(id => !_ended.Contains(id)).ToList()) { _ended.Add(callId); events.Add(new StreamToolUseEnd(callId)); }
                events.Add(new StreamMessageEnd(usage, type == "response.incomplete" ? "max_tokens" : _sawTool ? "tool_use" : "end_turn"));
                _endEmitted = true;
                Finished = true;
                break;
            }
            case "response.failed":
            case "error":
            {
                var message = root.TryGetProperty("response", out var r) && r.TryGetProperty("error", out var e) ? Str(e, "message")
                    : root.TryGetProperty("error", out var e2) ? (e2.ValueKind == JsonValueKind.Object ? Str(e2, "message") : e2.GetString())
                    : Str(root, "message");
                throw new HttpRequestException($"ChatGPT error: {message ?? "the response failed"}");
            }
        }
        return events;
    }

    public IEnumerable<StreamEvent> Finish()
    {
        if (_endEmitted) yield break;
        // The stream ended without a completion event: treat it as a failure rather than an empty answer.
        throw new HttpRequestException("ChatGPT stream ended before the response completed");
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}

public sealed record CodexCredentials(string AccessToken, string? AccountId);

public interface ICodexAuthSource
{
    Task<CodexCredentials> GetAsync(bool forceRefresh, CancellationToken ct);
}

/// <summary>Reads Codex's saved ChatGPT login; delegates every refresh to Codex itself.</summary>
public sealed class CodexAuthSource : ICodexAuthSource
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private readonly string _authPath;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public CodexAuthSource(string? authPath = null)
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        _authPath = authPath ?? Path.Combine(string.IsNullOrEmpty(home) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : home, "auth.json");
    }

    public async Task<CodexCredentials> GetAsync(bool forceRefresh, CancellationToken ct)
    {
        var (creds, expires) = Read();
        if (!forceRefresh && (expires is null || expires > DateTimeOffset.UtcNow + RefreshMargin)) return creds;
        await _refreshLock.WaitAsync(ct);
        try
        {
            var (again, againExpires) = Read();
            // another caller may already have refreshed
            if (!forceRefresh && (againExpires is null || againExpires > DateTimeOffset.UtcNow + RefreshMargin)) return again;
            if (forceRefresh && again.AccessToken != creds.AccessToken) return again;
            using (var server = await CodexAppServer.StartAsync(ct))
                await server.RequestAsync("account/read", new JsonObject { ["refreshToken"] = true }, ct);
            return Read().Creds;
        }
        finally { _refreshLock.Release(); }
    }

    private (CodexCredentials Creds, DateTimeOffset? Expires) Read()
    {
        if (!File.Exists(_authPath))
            throw new InvalidOperationException($"Codex is not signed in ({_authPath} not found). Run `codex login` and choose ChatGPT.");
        using var doc = JsonDocument.Parse(File.ReadAllText(_authPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object
            || !tokens.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(at.GetString()))
            throw new InvalidOperationException("Codex is not signed in with ChatGPT. Run `codex login` and choose ChatGPT.");
        var access = at.GetString()!;
        string? account = tokens.TryGetProperty("account_id", out var acc) && acc.ValueKind == JsonValueKind.String ? acc.GetString() : null;
        if (string.IsNullOrEmpty(account) && tokens.TryGetProperty("id_token", out var idt) && idt.ValueKind == JsonValueKind.String)
            account = JwtClaim(idt.GetString()!, "https://api.openai.com/auth", "chatgpt_account_id");
        return (new CodexCredentials(access, account), JwtExpiry(access));
    }

    internal static DateTimeOffset? JwtExpiry(string jwt)
    {
        var payload = JwtPayload(jwt);
        return payload is { } p && p.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
    }

    private static string? JwtClaim(string jwt, string ns, string name)
    {
        var payload = JwtPayload(jwt);
        return payload is { } p && p.TryGetProperty(ns, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static JsonElement? JwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }
}

/// <summary>Short-lived `codex app-server` (JSON-RPC over stdio) for model listing and login refresh.</summary>
internal sealed class CodexAppServer : IDisposable
{
    private readonly Process _proc;
    private int _nextId = 1;

    private CodexAppServer(Process proc) { _proc = proc; }

    public static async Task<CodexAppServer> StartAsync(CancellationToken ct)
    {
        var bin = Environment.GetEnvironmentVariable("SOLVRA_CODEX_BIN") ?? FindCodex();
        var psi = new ProcessStartInfo(bin, "app-server")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start codex app-server");
        _ = proc.StandardError.ReadToEndAsync();   // drain
        var server = new CodexAppServer(proc);
        await server.RequestAsync("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "solvra", ["title"] = "Solvra", ["version"] = "1.0" }, ["capabilities"] = null }, ct);
        await server.WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized" });
        return server;
    }

    private static string FindCodex()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var npm = Path.Combine(home, ".npm-global", "bin", "codex");
        if (File.Exists(npm)) return npm;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "codex"))) return Path.Combine(dir, "codex");
        return "codex";
    }

    private async Task WriteAsync(JsonObject msg)
    {
        await _proc.StandardInput.WriteLineAsync(msg.ToJsonString());
        await _proc.StandardInput.FlushAsync();
    }

    public async Task<JsonElement> RequestAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        var id = _nextId++;
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var line = await _proc.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException($"codex app-server exited during {method}");
            if (!line.StartsWith('{')) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var rid) || rid.ValueKind != JsonValueKind.Number || rid.GetInt32() != id || root.TryGetProperty("method", out _)) continue;
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"codex {method} failed: {(err.TryGetProperty("message", out var m) ? m.GetString() : err.GetRawText())}");
            return root.TryGetProperty("result", out var result) ? result.Clone() : default;
        }
    }

    public void Dispose()
    {
        try { _proc.StandardInput.Close(); if (!_proc.WaitForExit(2000)) _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _proc.Dispose();
    }
}
