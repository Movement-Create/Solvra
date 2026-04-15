using System.Text.Json;
using Solvra.Hooks;
using Solvra.Memory;
using Solvra.Models;
using Solvra.Observability;
using Solvra.Providers;
using Solvra.Security;
using Solvra.Skills;
using Solvra.Tools;

namespace Solvra.Core;

public sealed class AgentLoop
{
    private readonly ModelRouter _router;
    private readonly IToolRegistry _toolRegistry;
    private readonly CostTracker _costTracker;
    private readonly SessionManager _sessionManager;
    private readonly HookEngine _hookEngine;
    private readonly AuditLogger _auditLogger;
    private readonly SkillLoader? _skillLoader;
    private readonly MemoryManager? _memoryManager;
    private readonly PermissionChecker _permissionChecker;
    private readonly Tracer _tracer;

    public AgentLoop(
        ModelRouter router,
        IToolRegistry toolRegistry,
        HookEngine? hookEngine = null,
        AuditLogger? auditLogger = null,
        SkillLoader? skillLoader = null,
        MemoryManager? memoryManager = null,
        PermissionChecker? permissionChecker = null,
        CostTracker? costTracker = null,
        SessionManager? sessionManager = null,
        Tracer? tracer = null)
    {
        _router = router;
        _toolRegistry = toolRegistry;
        _hookEngine = hookEngine ?? new HookEngine();
        _auditLogger = auditLogger ?? new AuditLogger("logs");
        _skillLoader = skillLoader;
        _memoryManager = memoryManager;
        _permissionChecker = permissionChecker ?? new PermissionChecker();
        _costTracker = costTracker ?? new CostTracker();
        _sessionManager = sessionManager ?? new SessionManager();
        _tracer = tracer ?? new Tracer("AgentLoop");
    }

    public async Task<AgentRunResult> RunAsync(AgentRunOptions options, CancellationToken ct = default)
    {
        var (provider, resolvedModel) = _router.Resolve(options.Session.Model, options.Session.Provider);
        var sessionId = options.Session.Id;
        var permissionMode = Enum.TryParse<PermissionMode>(options.Session.PermissionMode, true, out var pm)
            ? pm
            : PermissionMode.Default;

        // Audit: session start
        await _auditLogger.LogAsync("SessionStart", new { sessionId, model = resolvedModel, provider = provider.Id }, sessionId);

        // Build message history
        var messages = new List<Message>();
        if (options.History != null)
            messages.AddRange(options.History);
        messages.Add(Message.FromText(MessageRole.User, options.Prompt));

        // Assemble system prompt with skills, lessons, and facts
        string? solvraMarkdown = null;
        foreach (var name in new[] { "SOLVRA.md", ".solvra.md" })
        {
            if (File.Exists(name))
            {
                solvraMarkdown = await File.ReadAllTextAsync(name, ct);
                break;
            }
        }

        IReadOnlyList<string>? skillContents = null;
        IReadOnlyList<string>? lessonContents = null;
        string? memoryFacts = null;

        if (_skillLoader != null)
        {
            var skills = await _skillLoader.GetRelevantSkillsAsync(options.Prompt);
            skillContents = skills.Select(s => s.Content).ToList();
        }

        if (_memoryManager != null)
        {
            var lessons = await _memoryManager.GetRelevantLessonsAsync(options.Prompt);
            lessonContents = lessons.Select(l => $"[{l.Date}] [{string.Join(", ", l.Tags)}] {l.Content}").ToList();
            memoryFacts = await _memoryManager.LoadFactsAsync();
        }

        var systemPrompt = Context.AssembleContext(
            options.SystemPrompt ?? options.Session.SystemPrompt,
            solvraMarkdown,
            skills: skillContents,
            lessons: lessonContents,
            memoryFacts: memoryFacts
        );

        var turns = 0;
        var totalUsage = new TokenUsage();
        var lastText = "";

        // --- THE LOOP ---
        while (turns < options.Session.MaxTurns)
        {
            ct.ThrowIfCancellationRequested();
            turns++;
            using var turnSpan = _tracer.StartSpan("agent.turn", new Dictionary<string, object> { ["turn"] = turns, ["tokens"] = totalUsage.InputTokens + totalUsage.OutputTokens });

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
                await LogSessionEnd(sessionId, turns, currentCost, "MaxBudget");
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

                // Fire stop hook
                var stopResult = await _hookEngine.FireStopAsync(sessionId, turns, lastText);
                await LogSessionEnd(sessionId, turns, currentCost, "Text");
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

                // Convert input dict to JsonElement
                var inputElement = JsonSerializer.SerializeToElement(tc.Input);

                // Fire PreToolUse hook
                var hookInfo = new ToolCallInfo(tc.Id, tc.Name, inputElement);
                var preHookResult = await _hookEngine.FirePreToolUseAsync(sessionId, turns, hookInfo);

                if (preHookResult.Action == HookAction.Block)
                {
                    toolResults.Add(new ToolResultContent
                    {
                        ToolUseId = tc.Id,
                        Name = tc.Name,
                        Content = $"[Blocked by hook] {preHookResult.Reason ?? "Tool execution blocked."}",
                        IsError = true
                    });
                    continue;
                }

                // Use modified input if hook says so
                var effectiveInput = preHookResult.Action == HookAction.Modify && preHookResult.ModifiedInput.HasValue
                    ? preHookResult.ModifiedInput.Value
                    : inputElement;

                // Permission check
                var permAllowed = await CheckToolPermission(tc.Name, permissionMode, options.OnPermissionRequest, tc);
                if (!permAllowed)
                {
                    toolResults.Add(new ToolResultContent
                    {
                        ToolUseId = tc.Id,
                        Name = tc.Name,
                        Content = "Permission denied by user.",
                        IsError = true
                    });
                    continue;
                }

                // Build execution context
                var execContext = new ToolExecutionContext(
                    SessionId: sessionId,
                    Cwd: Directory.GetCurrentDirectory(),
                    PlanMode: permissionMode == PermissionMode.Plan,
                    Env: new Dictionary<string, string>(),
                    Session: new Tools.SessionInfo(
                        sessionId,
                        options.Session.PermissionMode,
                        options.Session.AllowedTools.ToList(),
                        options.Session.DisallowedTools.ToList()));

                // Execute tool
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ToolExecuteResult result;
                using (var toolSpan = _tracer.StartSpan("tool.execute", new Dictionary<string, object> { ["tool"] = tc.Name, ["input_length"] = effectiveInput.GetRawText().Length }))
                {
                    result = await _toolRegistry.ExecuteToolAsync(tc.Name, effectiveInput, execContext, ct);
                    sw.Stop();
                    _tracer.AddEvent("tool.result", new Dictionary<string, object> { ["tool"] = tc.Name, ["is_error"] = result.IsError, ["duration_ms"] = sw.ElapsedMilliseconds });
                }

                var toolResult = new ToolResult
                {
                    ToolUseId = tc.Id,
                    Content = result.Output,
                    IsError = result.IsError
                };
                options.OnToolResult?.Invoke(toolResult);

                // Fire PostToolUse hook
                var resultInfo = new ToolResultInfo(result.Output, result.IsError);
                await _hookEngine.FirePostToolUseAsync(sessionId, turns, hookInfo, resultInfo);

                // Audit log tool execution
                await _auditLogger.LogAsync("ToolExecution", new
                {
                    sessionId,
                    turn = turns,
                    toolName = tc.Name,
                    isError = result.IsError,
                    outputPreview = result.Output[..Math.Min(500, result.Output.Length)]
                }, sessionId);

                toolResults.Add(new ToolResultContent
                {
                    ToolUseId = tc.Id,
                    Name = tc.Name,
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
        await LogSessionEnd(sessionId, turns, finalCost, "MaxTurns");
        return BuildResult(lastText, turns, totalUsage, finalCost, StopReason.MaxTurns, messages);
    }

    private async Task<bool> CheckToolPermission(
        string toolName,
        PermissionMode mode,
        Func<ToolCall, Task<bool>>? onPermissionRequest,
        ToolCall tc)
    {
        if (mode == PermissionMode.Auto || mode == PermissionMode.BypassPermissions)
            return true;

        if (_toolRegistry is ToolRegistry registry)
        {
            var tool = registry.GetTool(toolName);
            if (tool != null)
            {
                Func<ITool, Task<bool>>? callback = onPermissionRequest != null
                    ? async (t) => await onPermissionRequest(tc)
                    : null;
                return await _permissionChecker.CheckPermissionAsync(tool, mode, callback);
            }
        }

        // Fallback: if callback exists, ask user
        if (onPermissionRequest != null)
            return await onPermissionRequest(tc);

        return mode == PermissionMode.Auto;
    }

    private async Task LogSessionEnd(string sessionId, int turns, decimal costUsd, string stopReason)
    {
        await _auditLogger.LogAsync("SessionEnd", new { sessionId, turns, costUsd, stopReason }, sessionId);
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
