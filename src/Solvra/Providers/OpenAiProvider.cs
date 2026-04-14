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

    private static readonly Dictionary<string, (decimal Input, decimal Output)> Pricing = new()
    {
        ["gpt-4o"] = (5m, 15m),
        ["gpt-4o-mini"] = (0.15m, 0.6m),
        ["gpt-4-turbo"] = (10m, 30m),
        ["gpt-3.5-turbo"] = (0.5m, 1.5m),
        ["o1-preview"] = (15m, 60m),
        ["o1-mini"] = (3m, 12m),
    };

    private static readonly (decimal Input, decimal Output) DefaultPricing = (5m, 15m);

    public string Id => "openai";
    public string DisplayName => "OpenAI";

    public OpenAiProvider(HttpClient? http = null, string? apiKey = null, string? baseUrl = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        _baseUrl = baseUrl
            ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
            ?? "https://api.openai.com/v1";
        _http = http ?? new HttpClient();
    }

    public async Task<LlmResponse> CompleteAsync(CompletionOptions options, CancellationToken ct = default)
    {
        var request = BuildRequest(options);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        httpRequest.Content = JsonContent.Create(request);
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OpenAI API error {(int)response.StatusCode}: {body}",
                null, response.StatusCode);
        }

        return ParseResponse(body);
    }

    public async IAsyncEnumerable<string> StreamAsync(CompletionOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = BuildRequest(options);
        request["stream"] = true;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        httpRequest.Content = JsonContent.Create(request);
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    yield return content.GetString() ?? "";
                }
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

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
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
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
                if (!string.IsNullOrEmpty(joinedText))
                    obj["content"] = joinedText;

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
                                ["arguments"] = JsonSerializer.Serialize(tu.Input)
                            }
                        });
                    }
                    obj["tool_calls"] = tcArr;
                }

                messages.Add(obj);
                continue;
            }

            // User messages
            var userText = msg.GetTextContent();
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = userText });
        }

        var request = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = messages,
            ["max_tokens"] = options.MaxTokens,
        };

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

        if (options.Temperature.HasValue)
            request["temperature"] = (JsonNode)options.Temperature.Value;

        return request;
    }

    private static LlmResponse ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        string? text = null;
        if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            text = contentEl.GetString();

        var toolCalls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var tcArr))
        {
            foreach (var tc in tcArr.EnumerateArray())
            {
                var func = tc.GetProperty("function");
                var argsStr = func.GetProperty("arguments").GetString() ?? "{}";
                var input = ParseToolArguments(argsStr);
                toolCalls.Add(new ToolCall
                {
                    Id = tc.GetProperty("id").GetString() ?? "",
                    Name = func.GetProperty("name").GetString() ?? "",
                    Input = input
                });
            }
        }

        var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() ?? "stop" : "stop";

        var usage = root.GetProperty("usage");
        return new LlmResponse
        {
            Text = text,
            ToolCalls = toolCalls,
            StopReason = finishReason,
            Usage = new TokenUsage
            {
                InputTokens = usage.GetProperty("prompt_tokens").GetInt32(),
                OutputTokens = usage.GetProperty("completion_tokens").GetInt32()
            }
        };
    }

    internal static Dictionary<string, JsonElement> ParseToolArguments(string raw)
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
            if (start < 0) return new Dictionary<string, JsonElement>();

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
                        return new Dictionary<string, JsonElement>();
                    }
                }
            }
            return new Dictionary<string, JsonElement>();
        }
    }
}
