using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solvra.Models;

namespace Solvra.Providers;

public sealed class OllamaProvider : IProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public string Id => "ollama";
    public string DisplayName => "Ollama (Local)";

    public OllamaProvider(HttpClient? http = null, string? baseUrl = null)
    {
        _baseUrl = baseUrl
            ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
            ?? "http://localhost:11434";
        _http = http ?? new HttpClient();
    }

    public async Task<LlmResponse> CompleteAsync(CompletionOptions options, CancellationToken ct = default)
    {
        var request = BuildRequest(options, stream: false);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat");
        httpRequest.Content = JsonContent.Create(request);

        using var response = await _http.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Ollama API error {(int)response.StatusCode}: {body}",
                null, response.StatusCode);
        }

        return ParseResponse(body);
    }

    public async IAsyncEnumerable<string> StreamAsync(CompletionOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = BuildRequest(options, stream: true);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat");
        httpRequest.Content = JsonContent.Create(request);

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("done", out var done) && done.GetBoolean())
                break;

            if (root.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var models = new List<string>();
        if (doc.RootElement.TryGetProperty("models", out var modelsArr))
        {
            foreach (var model in modelsArr.EnumerateArray())
            {
                if (model.TryGetProperty("name", out var name))
                    models.Add(name.GetString() ?? "");
            }
        }
        return models;
    }

    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public decimal EstimateCost(string model, int inputTokens, int outputTokens) => 0m;

    private JsonObject BuildRequest(CompletionOptions options, bool stream)
    {
        var messages = new JsonArray();

        if (!string.IsNullOrEmpty(options.System))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = options.System });
        }

        foreach (var msg in options.Messages)
        {
            if (msg.Role == MessageRole.System) continue;

            if (msg.Role == MessageRole.Assistant)
            {
                var toolUses = msg.Content.OfType<ToolUseContent>().ToList();
                if (toolUses.Count > 0)
                {
                    var obj = new JsonObject { ["role"] = "assistant" };
                    var textContent = msg.GetTextContent();
                    if (!string.IsNullOrEmpty(textContent))
                        obj["content"] = textContent;

                    var tcArr = new JsonArray();
                    foreach (var tu in toolUses)
                    {
                        tcArr.Add(new JsonObject
                        {
                            ["function"] = new JsonObject
                            {
                                ["name"] = tu.Name,
                                ["arguments"] = JsonNode.Parse(JsonSerializer.Serialize(tu.Input))
                            }
                        });
                    }
                    obj["tool_calls"] = tcArr;
                    messages.Add(obj);
                    continue;
                }
            }

            if (msg.Role == MessageRole.Tool)
            {
                foreach (var block in msg.Content)
                {
                    if (block is ToolResultContent tr)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["content"] = tr.Content
                        });
                    }
                }
                continue;
            }

            var text = ExtractText(msg);
            messages.Add(new JsonObject
            {
                ["role"] = msg.Role.ToWireString(),
                ["content"] = text
            });
        }

        var request = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["options"] = new JsonObject { ["num_predict"] = options.MaxTokens }
        };

        if (options.Temperature.HasValue)
        {
            request["options"]!.AsObject()["temperature"] = (JsonNode)options.Temperature.Value;
        }

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
        }

        return request;
    }

    private static string ExtractText(Message msg)
    {
        var parts = new List<string>();
        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextContent tc:
                    parts.Add(tc.Text);
                    break;
                case ToolResultContent tr:
                    parts.Add(tr.Content);
                    break;
                case ToolUseContent tu:
                    parts.Add($"[Tool call: {tu.Name}({JsonSerializer.Serialize(tu.Input)})]");
                    break;
            }
        }
        return string.Join("\n", parts);
    }

    private static LlmResponse ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string? text = null;
        var toolCalls = new List<ToolCall>();

        if (root.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString();
            }

            if (message.TryGetProperty("tool_calls", out var tcArr))
            {
                int i = 0;
                foreach (var tc in tcArr.EnumerateArray())
                {
                    var func = tc.GetProperty("function");
                    var input = new Dictionary<string, JsonElement>();
                    if (func.TryGetProperty("arguments", out var args))
                    {
                        foreach (var prop in args.EnumerateObject())
                            input[prop.Name] = prop.Value.Clone();
                    }
                    toolCalls.Add(new ToolCall
                    {
                        Id = $"ollama_call_{i}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        Name = func.GetProperty("name").GetString() ?? "",
                        Input = input
                    });
                    i++;
                }
            }
        }

        var stopReason = root.TryGetProperty("done_reason", out var dr) ? dr.GetString() ?? "stop" : "stop";
        int inputTokens = root.TryGetProperty("prompt_eval_count", out var pec) ? pec.GetInt32() : 0;
        int outputTokens = root.TryGetProperty("eval_count", out var ec) ? ec.GetInt32() : 0;

        return new LlmResponse
        {
            Text = text,
            ToolCalls = toolCalls,
            StopReason = stopReason,
            Usage = new TokenUsage { InputTokens = inputTokens, OutputTokens = outputTokens }
        };
    }
}
