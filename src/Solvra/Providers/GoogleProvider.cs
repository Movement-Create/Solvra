using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solvra.Models;

namespace Solvra.Providers;

public sealed class GoogleProvider : IProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly Dictionary<string, (decimal Input, decimal Output)> Pricing = new()
    {
        ["gemini-2.5-pro"] = (1.25m, 10.0m),
        ["gemini-2.5-flash"] = (0.15m, 0.6m),
        ["gemini-2.0-flash"] = (0.1m, 0.4m),
        ["gemini-2.0-flash-lite"] = (0.025m, 0.1m),
    };

    private static readonly (decimal Input, decimal Output) DefaultPricing = (3.5m, 10.5m);

    private static readonly HashSet<string> PropertiesToStrip =
    [
        "additionalProperties", "$schema", "$ref", "$defs",
        "allOf", "anyOf", "oneOf", "not", "default", "examples"
    ];

    public string Id => "google";
    public string DisplayName => "Google Gemini";

    public GoogleProvider(HttpClient? http = null, string? apiKey = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? "";
        _http = http ?? new HttpClient();
    }

    public async Task<LlmResponse> CompleteAsync(CompletionOptions options, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{options.Model}:generateContent?key={_apiKey}";
        var request = BuildRequest(options);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Content = JsonContent.Create(request);

        using var response = await _http.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Google API error {(int)response.StatusCode}: {body}",
                null, response.StatusCode);
        }

        return ParseResponse(body);
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(CompletionOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{options.Model}:streamGenerateContent?key={_apiKey}&alt=sse";
        var request = BuildRequest(options);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Content = JsonContent.Create(request);

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Google API error {(int)response.StatusCode}: {errorBody}",
                null, response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string stopReason = "STOP";
        int inputTokens = 0, outputTokens = 0;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                if (usage.TryGetProperty("promptTokenCount", out var pt)) inputTokens = pt.GetInt32();
                if (usage.TryGetProperty("candidatesTokenCount", out var ct2)) outputTokens = ct2.GetInt32();
            }

            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];

                if (candidate.TryGetProperty("finishReason", out var fr))
                    stopReason = fr.GetString() ?? "STOP";

                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text))
                        {
                            yield return new StreamText(text.GetString() ?? "");
                        }
                        else if (part.TryGetProperty("functionCall", out var fc))
                        {
                            var name = fc.GetProperty("name").GetString() ?? "";
                            var callId = $"call_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(1000, 9999)}";
                            var argsJson = fc.TryGetProperty("args", out var args)
                                ? args.GetRawText()
                                : "{}";

                            yield return new StreamToolUseStart(callId, name);
                            yield return new StreamToolUseDelta(callId, argsJson);
                            yield return new StreamToolUseEnd(callId);
                        }
                    }
                }
            }
        }

        yield return new StreamMessageEnd(
            new TokenUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
            stopReason);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        return
        [
            "gemini-2.5-pro",
            "gemini-2.5-flash",
            "gemini-2.0-flash",
            "gemini-2.0-flash-lite"
        ];
    }

    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey)) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}?key={_apiKey}");
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
        var contents = new JsonArray();

        foreach (var msg in options.Messages)
        {
            if (msg.Role == MessageRole.System) continue;

            var wireRole = msg.Role switch
            {
                MessageRole.Assistant => "model",
                MessageRole.Tool => "user",
                _ => "user"
            };

            var parts = new JsonArray();
            foreach (var block in msg.Content)
            {
                switch (block)
                {
                    case TextContent tc:
                        parts.Add(new JsonObject { ["text"] = tc.Text });
                        break;
                    case ToolUseContent tu:
                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = tu.Name,
                                ["args"] = JsonNode.Parse(JsonSerializer.Serialize(tu.Input))
                            }
                        });
                        break;
                    case ToolResultContent tr:
                        parts.Add(new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = tr.Name ?? tr.ToolUseId,
                                ["response"] = new JsonObject { ["result"] = tr.Content }
                            }
                        });
                        break;
                }
            }

            contents.Add(new JsonObject { ["role"] = wireRole, ["parts"] = parts });
        }

        var request = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = options.MaxTokens }
        };

        if (options.Temperature.HasValue)
        {
            var genConfig = request["generationConfig"]!.AsObject();
            genConfig["temperature"] = (JsonNode)options.Temperature.Value;
        }

        if (!string.IsNullOrEmpty(options.System))
        {
            request["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = options.System } }
            };
        }

        if (options.Tools is { Count: > 0 })
        {
            var declarations = new JsonArray();
            foreach (var tool in options.Tools)
            {
                var sanitized = SanitizeSchema(JsonNode.Parse(JsonSerializer.Serialize(tool.InputSchema)));
                declarations.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = sanitized
                });
            }
            request["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = declarations } };
        }

        return request;
    }

    private static LlmResponse ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var textParts = new List<string>();
        var toolCalls = new List<ToolCall>();
        var stopReason = "STOP";
        int inputTokens = 0, outputTokens = 0;

        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (candidate.TryGetProperty("finishReason", out var fr))
                stopReason = fr.GetString() ?? "STOP";

            if (candidate.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text))
                    {
                        textParts.Add(text.GetString() ?? "");
                    }
                    else if (part.TryGetProperty("functionCall", out var fc))
                    {
                        var name = fc.GetProperty("name").GetString() ?? "";
                        var input = new Dictionary<string, JsonElement>();
                        if (fc.TryGetProperty("args", out var args))
                        {
                            foreach (var prop in args.EnumerateObject())
                                input[prop.Name] = prop.Value.Clone();
                        }
                        toolCalls.Add(new ToolCall
                        {
                            Id = $"call_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(1000, 9999)}",
                            Name = name,
                            Input = input
                        });
                    }
                }
            }
        }

        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            inputTokens = usage.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0;
            outputTokens = usage.TryGetProperty("candidatesTokenCount", out var ct2) ? ct2.GetInt32() : 0;
        }

        return new LlmResponse
        {
            Text = textParts.Count > 0 ? string.Join("", textParts) : null,
            ToolCalls = toolCalls,
            StopReason = stopReason,
            Usage = new TokenUsage { InputTokens = inputTokens, OutputTokens = outputTokens }
        };
    }

    private static JsonNode? SanitizeSchema(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var clean = new JsonObject();
            foreach (var kvp in obj)
            {
                if (PropertiesToStrip.Contains(kvp.Key)) continue;
                clean[kvp.Key] = SanitizeSchema(kvp.Value?.DeepClone());
            }
            return clean;
        }
        if (node is JsonArray arr)
        {
            var clean = new JsonArray();
            foreach (var item in arr)
                clean.Add(SanitizeSchema(item?.DeepClone()));
            return clean;
        }
        return node?.DeepClone();
    }
}
