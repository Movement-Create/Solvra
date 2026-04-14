using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solvra.Models;

namespace Solvra.Providers;

public sealed class AnthropicProvider : IProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private const string BaseUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private static readonly Dictionary<string, (decimal Input, decimal Output)> Pricing = new()
    {
        ["claude-3-5-sonnet-20241022"] = (3m, 15m),
        ["claude-3-5-haiku-20241022"] = (0.8m, 4m),
        ["claude-3-opus-20240229"] = (15m, 75m),
        ["claude-3-sonnet-20240229"] = (3m, 15m),
        ["claude-3-haiku-20240307"] = (0.25m, 1.25m),
    };

    private static readonly (decimal Input, decimal Output) DefaultPricing = (5m, 15m);

    public string Id => "anthropic";
    public string DisplayName => "Anthropic";

    public AnthropicProvider(HttpClient? http = null, string? apiKey = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        _http = http ?? new HttpClient();
    }

    public async Task<LlmResponse> CompleteAsync(CompletionOptions options, CancellationToken ct = default)
    {
        var request = BuildRequest(options);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        httpRequest.Content = JsonContent.Create(request);
        httpRequest.Headers.Add("x-api-key", _apiKey);
        httpRequest.Headers.Add("anthropic-version", ApiVersion);

        using var response = await _http.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Anthropic API error {(int)response.StatusCode}: {body}",
                null, response.StatusCode);
        }

        return ParseResponse(body);
    }

    public async IAsyncEnumerable<string> StreamAsync(CompletionOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = BuildRequest(options);
        request["stream"] = true;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        httpRequest.Content = JsonContent.Create(request);
        httpRequest.Headers.Add("x-api-key", _apiKey);
        httpRequest.Headers.Add("anthropic-version", ApiVersion);

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
            if (root.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() == "content_block_delta" &&
                root.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("type", out var deltaType) &&
                deltaType.GetString() == "text_delta" &&
                delta.TryGetProperty("text", out var text))
            {
                yield return text.GetString() ?? "";
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        return
        [
            "claude-3-5-sonnet-20241022",
            "claude-3-5-haiku-20241022",
            "claude-3-opus-20240229",
            "claude-3-sonnet-20240229",
            "claude-3-haiku-20240307"
        ];
    }

    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey)) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
            var body = new JsonObject
            {
                ["model"] = "claude-3-haiku-20240307",
                ["max_tokens"] = 1,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = "hi" }
                }
            };
            request.Content = JsonContent.Create(body);
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", ApiVersion);

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
        foreach (var msg in options.Messages)
        {
            if (msg.Role == MessageRole.System) continue;

            var wireRole = msg.Role == MessageRole.Tool ? "user" : msg.Role.ToWireString();
            var contentArr = new JsonArray();

            foreach (var block in msg.Content)
            {
                switch (block)
                {
                    case TextContent tc:
                        contentArr.Add(new JsonObject { ["type"] = "text", ["text"] = tc.Text });
                        break;
                    case ToolUseContent tu:
                        contentArr.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = tu.Id,
                            ["name"] = tu.Name,
                            ["input"] = JsonNode.Parse(JsonSerializer.Serialize(tu.Input))
                        });
                        break;
                    case ToolResultContent tr:
                        contentArr.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = tr.ToolUseId,
                            ["content"] = tr.Content,
                            ["is_error"] = tr.IsError
                        });
                        break;
                    case ImageContent img:
                        var src = new JsonObject { ["type"] = img.Source.SourceType };
                        if (img.Source.SourceType == "base64")
                        {
                            src["media_type"] = img.Source.MediaType;
                            src["data"] = img.Source.Data;
                        }
                        else
                        {
                            src["url"] = img.Source.Url;
                        }
                        contentArr.Add(new JsonObject { ["type"] = "image", ["source"] = src });
                        break;
                }
            }

            messages.Add(new JsonObject { ["role"] = wireRole, ["content"] = contentArr });
        }

        var request = new JsonObject
        {
            ["model"] = options.Model,
            ["max_tokens"] = options.MaxTokens,
            ["messages"] = messages
        };

        if (!string.IsNullOrEmpty(options.System))
            request["system"] = options.System;

        if (options.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in options.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(JsonSerializer.Serialize(tool.InputSchema))
                });
            }
            request["tools"] = tools;
        }

        if (options.Temperature.HasValue)
            request["temperature"] = (JsonNode)options.Temperature.Value;

        return request;
    }

    private static LlmResponse ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var textParts = new List<string>();
        var toolCalls = new List<ToolCall>();

        if (root.TryGetProperty("content", out var content))
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type == "text")
                {
                    textParts.Add(block.GetProperty("text").GetString() ?? "");
                }
                else if (type == "tool_use")
                {
                    var inputDict = new Dictionary<string, JsonElement>();
                    if (block.TryGetProperty("input", out var input))
                    {
                        foreach (var prop in input.EnumerateObject())
                        {
                            inputDict[prop.Name] = prop.Value.Clone();
                        }
                    }
                    toolCalls.Add(new ToolCall
                    {
                        Id = block.GetProperty("id").GetString() ?? "",
                        Name = block.GetProperty("name").GetString() ?? "",
                        Input = inputDict
                    });
                }
            }
        }

        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "end_turn" : "end_turn";
        var usage = root.GetProperty("usage");

        return new LlmResponse
        {
            Text = textParts.Count > 0 ? string.Join("", textParts) : null,
            ToolCalls = toolCalls,
            StopReason = stopReason,
            Usage = new TokenUsage
            {
                InputTokens = usage.GetProperty("input_tokens").GetInt32(),
                OutputTokens = usage.GetProperty("output_tokens").GetInt32()
            }
        };
    }
}
