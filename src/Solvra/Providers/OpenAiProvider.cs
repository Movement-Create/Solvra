using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solvra.Models;

namespace Solvra.Providers;

public sealed class OpenAiProvider : IProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string? _sessionHeader;

    private static readonly Dictionary<string, (decimal Input, decimal Output)> Pricing = new()
    {
        ["gpt-4o"] = (5m, 15m),
        ["gpt-4o-mini"] = (0.15m, 0.6m),
        ["gpt-4-turbo"] = (10m, 30m),
        ["gpt-3.5-turbo"] = (0.5m, 1.5m),
        ["o1-preview"] = (15m, 60m),
        ["o1-mini"] = (3m, 12m),
        ["gpt-4.1"] = (2m, 8m),
        ["gpt-4.1-mini"] = (0.4m, 1.6m),
        ["o3"] = (2m, 8m),
        ["o4-mini"] = (1.1m, 4.4m),
    };

    /// <summary>
    /// Models without a known price cost 0: gateway / subscription models (opencode Zen, local
    /// servers) are not billed per token, and a made-up default price stopped runs on a fake budget.
    /// Set SOLVRA_PRICE_IN / SOLVRA_PRICE_OUT (USD per million tokens) to price them explicitly.
    /// </summary>
    private static (decimal Input, decimal Output) DefaultPricing =>
        (EnvDecimal("SOLVRA_PRICE_IN"), EnvDecimal("SOLVRA_PRICE_OUT"));

    private static decimal EnvDecimal(string name) =>
        decimal.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;

    /// <summary>Replayed tool arguments keep characters like &gt; and &amp; readable for the model.</summary>
    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Id => "openai";
    public string DisplayName => "OpenAI";

    public OpenAiProvider(HttpClient? http = null, string? apiKey = null, string? baseUrl = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        _baseUrl = baseUrl
            ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
            ?? "https://api.openai.com/v1";
        _http = http ?? new HttpClient();
        _sessionHeader = Environment.GetEnvironmentVariable("SOLVRA_OPENCODE_SESSION")
            ?? (_baseUrl.Contains("opencode.ai", StringComparison.OrdinalIgnoreCase)
                ? Guid.NewGuid().ToString()
                : null);
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        if (_sessionHeader is not null)
            request.Headers.Add("x-opencode-session", _sessionHeader);
    }

    public async Task<LlmResponse> CompleteAsync(CompletionOptions options, CancellationToken ct = default)
    {
        var request = BuildRequest(options);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        httpRequest.Content = JsonContent.Create(request);
        ApplyAuth(httpRequest);

        using var response = await _http.SendAsync(httpRequest, ct);
        await HttpErrors.EnsureSuccessAsync(response, "OpenAI", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(body);
    }

    /// <summary>
    /// Streams text and reasoning live. Tool calls are buffered per <c>index</c> and emitted once the
    /// stream ends: OpenAI-compatible backends differ in whether they repeat or blank the call id,
    /// send the name once or per chunk, or send usage after <c>finish_reason</c>, and emitting early
    /// lost the arguments for some of them (e.g. qwen via DashScope-style gateways).
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> StreamAsync(CompletionOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = BuildRequest(options);
        request["stream"] = true;
        if (StreamUsageEnabled)
            request["stream_options"] = new JsonObject { ["include_usage"] = true };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        httpRequest.Content = JsonContent.Create(request);
        ApplyAuth(httpRequest);

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await HttpErrors.EnsureSuccessAsync(response, "OpenAI", ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var assembler = new ToolCallAssembler();
        int inputTokens = 0, outputTokens = 0;
        string? finishReason = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; } // keep-alive or vendor noise

            using (doc)
            {
                var root = doc.RootElement;

                // Some gateways report errors inside the stream with a 200 status.
                if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind != JsonValueKind.Null)
                    throw new HttpRequestException($"OpenAI stream error: {HttpErrors.ExtractMessage(errEl.GetRawText()) ?? errEl.GetRawText()}");

                if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                {
                    if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number) inputTokens = pt.GetInt32();
                    if (usageEl.TryGetProperty("completion_tokens", out var ct2) && ct2.ValueKind == JsonValueKind.Number) outputTokens = ct2.GetInt32();
                }

                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                    continue;

                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    finishReason = fr.GetString();

                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                    continue;

                var reasoning = ReadReasoning(delta);
                if (!string.IsNullOrEmpty(reasoning))
                    yield return new StreamReasoning(reasoning);

                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return new StreamText(text);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                        assembler.Add(tc);
                }
            }
        }

        foreach (var call in assembler.Complete())
        {
            yield return new StreamToolUseStart(call.Id, call.Name);
            if (call.Arguments.Length > 0)
                yield return new StreamToolUseDelta(call.Id, call.Arguments);
            yield return new StreamToolUseEnd(call.Id);
        }

        yield return new StreamMessageEnd(
            new TokenUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
            NormalizeFinishReason(finishReason, assembler.Count > 0));
    }

    /// <summary>Set SOLVRA_OPENAI_STREAM_USAGE=0 for backends that reject <c>stream_options</c>.</summary>
    private static bool StreamUsageEnabled =>
        Environment.GetEnvironmentVariable("SOLVRA_OPENAI_STREAM_USAGE") is not ("0" or "false");

    /// <summary>Map OpenAI finish reasons onto the loop's vocabulary ("length" → "max_tokens").</summary>
    internal static string NormalizeFinishReason(string? reason, bool hasToolCalls) => reason switch
    {
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        null or "" => hasToolCalls ? "tool_use" : "end_turn",
        "stop" => hasToolCalls ? "tool_use" : "end_turn",
        _ => reason
    };

    private static string? ReadReasoning(JsonElement obj)
    {
        foreach (var name in new[] { "reasoning_content", "reasoning" })
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var v = el.GetString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        return null;
    }

    /// <summary>Accumulates streamed tool-call fragments keyed by index.</summary>
    internal sealed class ToolCallAssembler
    {
        internal sealed record Call(string Id, string Name, string Arguments);

        private sealed class Partial
        {
            public string? Id;
            public string Name = "";
            public readonly System.Text.StringBuilder Args = new();
        }

        private readonly SortedDictionary<int, Partial> _calls = new();
        private int _lastIndex = -1;

        public int Count => _calls.Count;

        public void Add(JsonElement tc)
        {
            var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) id = null;

            int index;
            if (tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number)
                index = idxEl.GetInt32();
            else if (id != null && !_calls.Values.Any(p => p.Id == id))
                index = _calls.Count == 0 ? 0 : _calls.Keys.Max() + 1;   // new call without an index
            else if (id != null)
                index = _calls.First(kv => kv.Value.Id == id).Key;
            else
                index = _lastIndex < 0 ? 0 : _lastIndex;
            _lastIndex = index;

            if (!_calls.TryGetValue(index, out var p))
                _calls[index] = p = new Partial();

            p.Id ??= id;

            if (tc.TryGetProperty("function", out var func) && func.ValueKind == JsonValueKind.Object)
            {
                if (func.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    var name = nameEl.GetString() ?? "";
                    // Most backends send the name once; a few repeat it on every chunk.
                    if (p.Name.Length == 0) p.Name = name;
                    else if (name.Length > 0 && name != p.Name && !p.Name.EndsWith(name)) p.Name += name;
                }

                if (func.TryGetProperty("arguments", out var argsEl))
                {
                    if (argsEl.ValueKind == JsonValueKind.String)
                        p.Args.Append(argsEl.GetString());
                    else if (argsEl.ValueKind == JsonValueKind.Object)
                    {
                        // Non-standard: a whole arguments object instead of a JSON string.
                        p.Args.Clear();
                        p.Args.Append(argsEl.GetRawText());
                    }
                }
            }
        }

        public IEnumerable<Call> Complete()
        {
            foreach (var (index, p) in _calls)
            {
                if (p.Name.Length == 0 && p.Args.Length == 0) continue;
                yield return new Call(p.Id ?? $"call_{index}_{Guid.NewGuid():N}"[..24], p.Name, p.Args.ToString());
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
        ApplyAuth(request);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var models = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id))
                    models.Add(id.GetString() ?? "");
            }
        }
        return models;
    }

    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey)) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
            ApplyAuth(request);
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public decimal EstimateCost(string model, int inputTokens, int outputTokens)
    {
        var pricing = Pricing.GetValueOrDefault(model, DefaultPricing);
        return (inputTokens * pricing.Input + outputTokens * pricing.Output) / 1_000_000m;
    }

    private JsonObject BuildRequest(CompletionOptions options)
    {
        var messages = new JsonArray();

        if (!string.IsNullOrEmpty(options.System))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = options.System });
        }

        foreach (var msg in options.Messages)
        {
            if (msg.Role == MessageRole.System) continue;

            if (msg.Role == MessageRole.Tool)
            {
                foreach (var block in msg.Content)
                {
                    if (block is ToolResultContent tr)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = tr.ToolUseId,
                            ["content"] = tr.Content
                        });
                    }
                }
                continue;
            }

            if (msg.Role == MessageRole.Assistant)
            {
                var textParts = msg.Content.OfType<TextContent>().Select(t => t.Text);
                var toolUses = msg.Content.OfType<ToolUseContent>().ToList();

                var obj = new JsonObject { ["role"] = "assistant" };
                var joinedText = string.Join("", textParts);
                // Some backends reject an assistant message with neither content nor tool_calls,
                // and others reject null content next to tool_calls, so always send a string.
                obj["content"] = joinedText;

                var reasoningText = string.Join("", msg.Content.OfType<ReasoningContent>().Select(r => r.Text));
                if (!string.IsNullOrEmpty(reasoningText))
                    obj["reasoning_content"] = reasoningText;

                if (toolUses.Count > 0)
                {
                    var tcArr = new JsonArray();
                    foreach (var tu in toolUses)
                    {
                        tcArr.Add(new JsonObject
                        {
                            ["id"] = tu.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = tu.Name,
                                ["arguments"] = JsonSerializer.Serialize(tu.Input, RelaxedJson)
                            }
                        });
                    }
                    obj["tool_calls"] = tcArr;
                }

                messages.Add(obj);
                continue;
            }

            // User messages — handle mixed text+image content
            var hasImage = msg.Content.OfType<ImageContent>().Any();
            if (hasImage)
            {
                var contentParts = new JsonArray();
                foreach (var part in msg.Content)
                {
                    if (part is TextContent textPart)
                        contentParts.Add(new JsonObject { ["type"] = "text", ["text"] = textPart.Text });
                    else if (part is ImageContent ic)
                    {
                        var imageUrl = ic.Source.Url ?? $"data:{ic.Source.MediaType};base64,{ic.Source.Data}";
                        contentParts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = imageUrl }
                        });
                    }
                }
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = contentParts });
            }
            else
            {
                var userText = msg.GetTextContent();
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = userText });
            }
        }

        var request = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = messages,
        };
        // OpenAI reasoning models (o1/o3/o4, gpt-5) reject max_tokens and temperature.
        var reasoningModel = IsOpenAiReasoningModel(options.Model);
        request[reasoningModel ? "max_completion_tokens" : "max_tokens"] = options.MaxTokens;

        if (options.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in options.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(JsonSerializer.Serialize(tool.InputSchema))
                    }
                });
            }
            request["tools"] = tools;
            request["tool_choice"] = "auto";
        }

        if (options.Temperature.HasValue && !reasoningModel)
            request["temperature"] = (JsonNode)options.Temperature.Value;

        return request;
    }

    internal static bool IsOpenAiReasoningModel(string model) =>
        System.Text.RegularExpressions.Regex.IsMatch(model, @"^(o\d|gpt-5)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static LlmResponse ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind != JsonValueKind.Null)
            throw new HttpRequestException($"OpenAI API error: {HttpErrors.ExtractMessage(errEl.GetRawText()) ?? errEl.GetRawText()}");

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new HttpRequestException("OpenAI API returned no choices");

        var choice = choices[0];
        var message = choice.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;

        string? text = null;
        string? reasoning = null;
        var toolCalls = new List<ToolCall>();

        if (message.ValueKind == JsonValueKind.Object)
        {
            if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                text = contentEl.GetString();
            reasoning = ReadReasoning(message);

            // "tool_calls": null is common from OpenAI-compatible servers.
            if (message.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var tc in tcArr.EnumerateArray())
                {
                    if (!tc.TryGetProperty("function", out var func) || func.ValueKind != JsonValueKind.Object) continue;
                    var argsStr = func.TryGetProperty("arguments", out var a)
                        ? a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.ValueKind == JsonValueKind.Object ? a.GetRawText() : ""
                        : "";
                    var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                    toolCalls.Add(new ToolCall
                    {
                        Id = string.IsNullOrEmpty(id) ? $"call_{i}_{Guid.NewGuid():N}"[..24] : id,
                        Name = func.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "",
                        Input = ParseToolArguments(argsStr)
                    });
                    i++;
                }
            }
        }

        var finishReason = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;

        int inTok = 0, outTok = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number) inTok = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ctk) && ctk.ValueKind == JsonValueKind.Number) outTok = ctk.GetInt32();
        }

        return new LlmResponse
        {
            Text = text,
            Reasoning = reasoning,
            ToolCalls = toolCalls,
            StopReason = NormalizeFinishReason(finishReason, toolCalls.Count > 0),
            Usage = new TokenUsage { InputTokens = inTok, OutputTokens = outTok }
        };
    }

    /// <summary>
    /// Key added to a parsed argument dictionary when the model's JSON could not be parsed.
    /// The agent loop turns it into a tool error so the model can retry, instead of silently
    /// running the tool with empty input.
    /// </summary>
    public const string ArgumentParseErrorKey = "__solvra_argument_error";

    public static Dictionary<string, JsonElement> ParseToolArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<string, JsonElement>();
        var parsed = TryParseArguments(raw);
        if (parsed != null) return parsed;
        var preview = raw.Length > 300 ? raw[..300] + "…" : raw;
        return new Dictionary<string, JsonElement>
        {
            [ArgumentParseErrorKey] = JsonSerializer.SerializeToElement(preview)
        };
    }

    private static Dictionary<string, JsonElement>? TryParseArguments(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw);
            var dict = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return dict;
        }
        catch
        {
            // Tolerant parser: try to extract first balanced JSON object
            var start = raw.IndexOf('{');
            if (start < 0) return null;

            var depth = 0;
            for (var i = start; i < raw.Length; i++)
            {
                if (raw[i] == '{') depth++;
                else if (raw[i] == '}') depth--;
                if (depth == 0)
                {
                    try
                    {
                        var sub = raw[start..(i + 1)];
                        var doc = JsonDocument.Parse(sub);
                        var dict = new Dictionary<string, JsonElement>();
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            dict[prop.Name] = prop.Value.Clone();
                        return dict;
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
            return null;
        }
    }
}
