using System.Text.Json;
using Solvra.Models;
using Solvra.Providers;

namespace Solvra.Core;

public sealed class AgentLoop
{
    private readonly ModelRouter _router;
    private readonly IToolRegistry _toolRegistry;
    private readonly CostTracker _costTracker;
    private readonly SessionManager _sessionManager;

    public AgentLoop(
        ModelRouter router,
        IToolRegistry toolRegistry,
        CostTracker? costTracker = null,
        SessionManager? sessionManager = null)
    {
        _router = router;
        _toolRegistry = toolRegistry;
        _costTracker = costTracker ?? new CostTracker();
        _sessionManager = sessionManager ?? new SessionManager();
    }

    public async Task<AgentRunResult> RunAsync(AgentRunOptions options, CancellationToken ct = default)
    {
        var (provider, resolvedModel) = _router.Resolve(options.Session.Model, options.Session.Provider);

        // Build message history
        var messages = new List<Message>();
        if (options.History != null)
            messages.AddRange(options.History);
        messages.Add(Message.FromText(MessageRole.User, options.Prompt));

        // Assemble system prompt
        string? solvraMarkdown = null;
        foreach (var name in new[] { "SOLVRA.md", ".solvra.md" })
        {
            if (File.Exists(name))
            {
                solvraMarkdown = await File.ReadAllTextAsync(name, ct);
                break;
            }
        }

        var systemPrompt = Context.AssembleContext(
            options.SystemPrompt ?? options.Session.SystemPrompt,
            solvraMarkdown,
            skills: null,
            lessons: null,
            memoryFacts: null
        );

        var turns = 0;
        var totalUsage = new TokenUsage();
        var lastText = "";

        // --- THE LOOP ---
        while (turns < options.Session.MaxTurns)
        {
            ct.ThrowIfCancellationRequested();
            turns++;

            // Get tool definitions
            var tools = _toolRegistry.GetToolDefinitions();

            // Compress context if needed
            var compressedMessages = Context.CompressContext(messages, resolvedModel, options.Session.Provider);

            // Call LLM with retry
            var response = await Retry.WithRetryAsync(
                async () => await provider.CompleteAsync(new CompletionOptions
                {
                    Model = resolvedModel,
                    Messages = compressedMessages,
                    System = systemPrompt,
                    Tools = tools.Count > 0 ? tools : null,
                    MaxTokens = 8192,
                    Stream = options.Streaming
                }, ct),
                new RetryOptions
                {
                    MaxRetries = 3,
                    OnRetry = async (attempt, ex) =>
                    {
                        options.OnText?.Invoke($"\n[Retry {attempt + 1}: {ex.Message}]\n");
                        return true;
                    }
                },
                ct
            );

            totalUsage += response.Usage;

            // Budget check
            var currentCost = provider.EstimateCost(resolvedModel, totalUsage.InputTokens, totalUsage.OutputTokens);
            if (currentCost > options.Session.MaxBudgetUsd)
            {
                lastText = response.Text ?? lastText;
                return BuildResult(lastText, turns, totalUsage, currentCost, StopReason.MaxBudget, messages);
            }

            // No tool calls → done
            if (response.ToolCalls.Count == 0)
            {
                lastText = response.Text ?? "";
                options.OnText?.Invoke(lastText);

                // Append assistant message
                messages.Add(Message.FromText(MessageRole.Assistant, lastText));

                await RecordCostAsync(options.Session, resolvedModel, provider, totalUsage, turns, currentCost);
                return BuildResult(lastText, turns, totalUsage, currentCost, StopReason.Text, messages);
            }

            // Tool calls present → build assistant message with mixed content
            var assistantContent = new List<MessageContent>();
            if (!string.IsNullOrEmpty(response.Text))
            {
                assistantContent.Add(new TextContent { Text = response.Text });
                options.OnText?.Invoke(response.Text);
            }

            foreach (var tc in response.ToolCalls)
            {
                assistantContent.Add(new ToolUseContent
                {
                    Id = tc.Id,
                    Name = tc.Name,
                    Input = tc.Input
                });
            }

            messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Content = assistantContent,
                Timestamp = DateTime.UtcNow.ToString("o")
            });

            // Execute each tool call
            var toolResults = new List<MessageContent>();
            foreach (var tc in response.ToolCalls)
            {
                options.OnToolCall?.Invoke(tc);

                // Permission check (if callback provided)
                if (options.OnPermissionRequest != null)
                {
                    var allowed = await options.OnPermissionRequest(tc);
                    if (!allowed)
                    {
                        toolResults.Add(new ToolResultContent
                        {
                            ToolUseId = tc.Id,
                            Content = "Permission denied by user.",
                            IsError = true
                        });
                        continue;
                    }
                }

                // Execute tool
                var result = await _toolRegistry.ExecuteToolAsync(tc.Name, tc.Input, ct);
                var toolResult = new ToolResult
                {
                    ToolUseId = tc.Id,
                    Content = result.Output,
                    IsError = result.IsError
                };
                options.OnToolResult?.Invoke(toolResult);

                toolResults.Add(new ToolResultContent
                {
                    ToolUseId = tc.Id,
                    Content = result.Output,
                    IsError = result.IsError
                });
            }

            // Append tool results
            messages.Add(new Message
            {
                Role = MessageRole.Tool,
                Content = toolResults,
                Timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        // Exhausted max_turns
        var finalCost = provider.EstimateCost(resolvedModel, totalUsage.InputTokens, totalUsage.OutputTokens);
        await RecordCostAsync(options.Session, resolvedModel, provider, totalUsage, turns, finalCost);
        return BuildResult(lastText, turns, totalUsage, finalCost, StopReason.MaxTurns, messages);
    }

    private static AgentRunResult BuildResult(
        string text, int turns, TokenUsage usage, decimal costUsd, StopReason stopReason, List<Message> messages)
    {
        return new AgentRunResult
        {
            Text = text,
            Turns = turns,
            Usage = usage,
            CostUsd = costUsd,
            StopReason = stopReason,
            Messages = messages
        };
    }

    private async Task RecordCostAsync(
        SessionConfig session, string model, IProvider provider, TokenUsage usage, int turns, decimal cost)
    {
        await _costTracker.RecordAsync(new CostEntry
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            SessionId = session.Id,
            Model = model,
            Provider = provider.Id,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CostUsd = cost,
            Turns = turns
        });
    }
}
