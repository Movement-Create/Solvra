using System.Text;
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
    /// <summary>How many times a reply cut off by max_tokens is continued automatically.</summary>
    private const int MaxLengthContinuations = 2;

    private readonly ModelRouter _router;
    private readonly IToolRegistry _toolRegistry;
    private readonly CostTracker _costTracker;
    private readonly SessionManager _sessionManager;
    private readonly HookEngine _hookEngine;
    private readonly AuditLogger _auditLogger;
    private readonly SkillLoader? _skillLoader;
    private readonly MemoryManager? _memoryManager;
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
        _auditLogger = auditLogger ?? new AuditLogger(Solvra.Config.SolvraPaths.LogsDir);
        _skillLoader = skillLoader;
        _memoryManager = memoryManager;
        _costTracker = costTracker ?? new CostTracker();
        _sessionManager = sessionManager ?? new SessionManager();
        _tracer = tracer ?? new Tracer(Path.Combine(Solvra.Config.SolvraPaths.LogsDir, "traces.jsonl"));
        // Permission checks are performed by the tool registry; the parameter is kept for API compatibility.
        _ = permissionChecker;
    }

    public async Task<AgentRunResult> RunAsync(AgentRunOptions options, CancellationToken ct = default)
    {
        var sessionId = options.Session.Id;
        var cwd = options.Cwd ?? Directory.GetCurrentDirectory();
        var messages = new List<Message>();
        if (options.History != null)
            messages.AddRange(options.History);
        messages.Add(Message.FromText(MessageRole.User, options.Prompt));

        if (options.LogToSession)
            await SafeLog(() => _sessionManager.LogUserMessageAsync(options.Session, options.Prompt));

        IProvider provider;
        string resolvedModel;
        try
        {
            (provider, resolvedModel) = _router.Resolve(options.Session.Model, options.Session.Provider);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync(options, sessionId, messages, "", 0, new TokenUsage(), 0m, $"Cannot use model '{options.Session.Model}': {ex.Message}");
        }

        var permissionMode = Enum.TryParse<PermissionMode>(options.Session.PermissionMode, true, out var pm)
            ? pm
            : PermissionMode.Default;

        await _auditLogger.LogAsync("SessionStart", new { sessionId, model = resolvedModel, provider = provider.Id }, sessionId);

        var systemPrompt = await BuildSystemPromptAsync(options, cwd, resolvedModel, ct);
        var tools = _toolRegistry.GetToolDefinitions();
        var overheadTokens = Context.EstimateTokens(systemPrompt) + Context.EstimateTokens(JsonSerializer.Serialize(tools));

        var turns = 0;
        var totalUsage = new TokenUsage();
        var lastText = "";
        var streamedAnyText = false;
        var lengthContinuations = 0;

        using var sessionSpan = _tracer.StartSpan("agent.session", new Dictionary<string, object>
        {
            ["session_id"] = sessionId,
            ["model"] = resolvedModel,
            ["provider"] = provider.Id
        });

        while (turns < options.Session.MaxTurns)
        {
            ct.ThrowIfCancellationRequested();
            turns++;
            using var turnSpan = _tracer.StartSpan("agent.turn", new Dictionary<string, object> { ["turn"] = turns, ["tokens"] = totalUsage.InputTokens + totalUsage.OutputTokens });

            var compressedMessages = Context.CompressContext(messages, resolvedModel, options.Session.Provider, overheadTokens);

            var completionOptions = new CompletionOptions
            {
                Model = resolvedModel,
                Messages = compressedMessages,
                System = systemPrompt,
                Tools = tools.Count > 0 ? tools : null,
                MaxTokens = options.Session.MaxTokens > 0 ? options.Session.MaxTokens : 8192,
                Stream = options.Streaming
            };

            // Separate this turn's text from the previous turn's in the live output.
            var separatorPending = streamedAnyText;

            LlmResponse response;
            try
            {
                using var llmSpan = _tracer.StartSpan("llm.call", new Dictionary<string, object>
                {
                    ["model"] = resolvedModel,
                    ["input_tokens"] = Context.EstimateContextTokens(compressedMessages) + overheadTokens
                });

                response = await Retry.WithRetryAsync(
                    async () => options.Streaming
                        ? await StreamOnceAsync(provider, completionOptions, compressedMessages, overheadTokens, delta =>
                        {
                            if (separatorPending) { options.OnText?.Invoke("\n\n"); separatorPending = false; }
                            streamedAnyText = true;
                            options.OnText?.Invoke(delta);
                        }, ct)
                        : await provider.CompleteAsync(completionOptions, ct),
                    new RetryOptions
                    {
                        MaxRetries = 3,
                        OnRetry = (attempt, ex) =>
                        {
                            Console.Error.WriteLine($"[retry {attempt + 1}/3] {ex.Message}");
                            return Task.FromResult(true);
                        }
                    },
                    ct);

                _tracer.AddEvent("llm.response", new Dictionary<string, object>
                {
                    ["output_tokens"] = response.Usage.OutputTokens,
                    ["stop_reason"] = response.StopReason
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var cost = provider.EstimateCost(resolvedModel, totalUsage.InputTokens, totalUsage.OutputTokens);
                return await FailAsync(options, sessionId, messages, lastText, turns, totalUsage, cost, ex.Message, provider, resolvedModel);
            }

            // Some OpenAI-compatible servers report no usage; estimate rather than record zero.
            if (response.Usage.InputTokens == 0 && response.Usage.OutputTokens == 0)
            {
                response = response with
                {
                    Usage = new TokenUsage
                    {
                        InputTokens = Context.EstimateContextTokens(compressedMessages) + overheadTokens,
                        OutputTokens = Context.EstimateTokens((response.Text ?? "") + (response.Reasoning ?? "") +
                            string.Join("", response.ToolCalls.Select(t => JsonSerializer.Serialize(t.Input))))
                    }
                };
            }

            totalUsage += response.Usage;

            var currentCost = provider.EstimateCost(resolvedModel, totalUsage.InputTokens, totalUsage.OutputTokens);

            // Non-streaming callers still get the text.
            if (!options.Streaming && !string.IsNullOrEmpty(response.Text))
            {
                if (streamedAnyText) options.OnText?.Invoke("\n\n");
                options.OnText?.Invoke(response.Text);
                streamedAnyText = true;
            }

            var assistantContent = new List<MessageContent>();
            if (!string.IsNullOrEmpty(response.Reasoning))
                assistantContent.Add(new ReasoningContent { Text = response.Reasoning });
            if (!string.IsNullOrEmpty(response.Text))
                assistantContent.Add(new TextContent { Text = response.Text });

            // No tool calls → the model is done, unless it was cut off mid-answer.
            if (response.ToolCalls.Count == 0)
            {
                var text = response.Text ?? "";
                if (response.StopReason == "max_tokens" && lengthContinuations < MaxLengthContinuations && turns < options.Session.MaxTurns)
                {
                    lengthContinuations++;
                    lastText += text;
                    messages.Add(new Message { Role = MessageRole.Assistant, Content = assistantContent.Count > 0 ? assistantContent : [new TextContent { Text = "(empty)" }], Timestamp = Now() });
                    messages.Add(Message.FromText(MessageRole.User, "Your reply was cut off by the output token limit. Continue exactly where you stopped, without repeating."));
                    continue;
                }

                lastText = lengthContinuations > 0 ? lastText + text : text;
                messages.Add(new Message { Role = MessageRole.Assistant, Content = assistantContent.Count > 0 ? assistantContent : [new TextContent { Text = text }], Timestamp = Now() });
                if (options.LogToSession)
                    await SafeLog(() => _sessionManager.LogAssistantMessageAsync(options.Session, lastText));

                await RecordCostAsync(options.Session, resolvedModel, provider, totalUsage, turns, currentCost);
                await _hookEngine.FireStopAsync(sessionId, turns, lastText);
                await LogSessionEnd(sessionId, turns, currentCost, "Text");
                return BuildResult(lastText, turns, totalUsage, currentCost, StopReason.Text, messages);
            }
            lengthContinuations = 0;

            foreach (var tc in response.ToolCalls)
            {
                assistantContent.Add(new ToolUseContent
                {
                    Id = tc.Id,
                    Name = tc.Name,
                    Input = tc.Input.ContainsKey(OpenAiProvider.ArgumentParseErrorKey) ? new Dictionary<string, JsonElement>() : tc.Input
                });
            }
            if (!string.IsNullOrEmpty(response.Text)) lastText = response.Text;

            var assistantMessage = new Message { Role = MessageRole.Assistant, Content = assistantContent, Timestamp = Now() };
            messages.Add(assistantMessage);
            if (options.LogToSession)
                await SafeLog(() => _sessionManager.LogAssistantTurnAsync(options.Session, assistantMessage));

            var toolResults = await ExecuteToolCallsAsync(response, options, sessionId, turns, cwd, permissionMode, ct);

            messages.Add(new Message
            {
                Role = MessageRole.Tool,
                Content = toolResults,
                Timestamp = Now()
            });

            // Budget check after the tool results are recorded, so history stays well-formed.
            if (options.Session.MaxBudgetUsd > 0 && currentCost > options.Session.MaxBudgetUsd)
            {
                await RecordCostAsync(options.Session, resolvedModel, provider, totalUsage, turns, currentCost);
                await LogSessionEnd(sessionId, turns, currentCost, "MaxBudget");
                var note = $"[Stopped: estimated cost ${currentCost:F2} exceeded the ${options.Session.MaxBudgetUsd:F2} budget. Raise it with --max-budget or SOLVRA_MAX_BUDGET.]";
                options.OnText?.Invoke("\n" + note + "\n");
                return BuildResult(Join(lastText, note), turns, totalUsage, currentCost, StopReason.MaxBudget, messages);
            }
        }

        var finalCost = provider.EstimateCost(resolvedModel, totalUsage.InputTokens, totalUsage.OutputTokens);
        await RecordCostAsync(options.Session, resolvedModel, provider, totalUsage, turns, finalCost);
        await LogSessionEnd(sessionId, turns, finalCost, "MaxTurns");
        var turnsNote = $"[Stopped after reaching the turn limit ({options.Session.MaxTurns}). Continue the session or raise --max-turns.]";
        options.OnText?.Invoke("\n" + turnsNote + "\n");
        return BuildResult(Join(lastText, turnsNote), turns, totalUsage, finalCost, StopReason.MaxTurns, messages);
    }

    private async Task<string> BuildSystemPromptAsync(AgentRunOptions options, string cwd, string model, CancellationToken ct)
    {
        IReadOnlyList<string>? skillContents = null;
        IReadOnlyList<string>? lessonContents = null;
        string? memoryFacts = null;

        try
        {
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[context] skills/memory unavailable: {ex.Message}");
        }

        var instructions = await Context.LoadProjectInstructionsAsync(cwd, ct);
        return Context.AssembleContext(
            options.SystemPrompt ?? options.Session.SystemPrompt,
            instructions,
            skills: skillContents,
            lessons: lessonContents,
            memoryFacts: memoryFacts,
            environment: Context.BuildEnvironmentInfo(cwd, model));
    }

    /// <summary>One streaming model call collected into an <see cref="LlmResponse"/>.</summary>
    private static async Task<LlmResponse> StreamOnceAsync(
        IProvider provider, CompletionOptions completionOptions, IReadOnlyList<Message> sent, int overheadTokens,
        Action<string> onText, CancellationToken ct)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        var args = new Dictionary<string, StringBuilder>();
        string stopReason = "end_turn";
        TokenUsage? usage = null;

        await foreach (var evt in provider.StreamAsync(completionOptions, ct))
        {
            switch (evt)
            {
                case StreamText t:
                    text.Append(t.Delta);
                    onText(t.Delta);
                    break;
                case StreamReasoning r:
                    reasoning.Append(r.Delta);
                    break;
                case StreamToolUseStart start:
                    if (toolCalls.Any(t => t.Id == start.Id)) break; // tolerate a repeated start
                    args[start.Id] = new StringBuilder();
                    toolCalls.Add(new ToolCall { Id = start.Id, Name = start.Name, Input = new Dictionary<string, JsonElement>() });
                    break;
                case StreamToolUseDelta delta:
                    if (args.TryGetValue(delta.Id, out var sb)) sb.Append(delta.JsonFragment);
                    break;
                case StreamToolUseEnd end:
                    if (args.TryGetValue(end.Id, out var argsSb))
                    {
                        var tc = toolCalls.First(t => t.Id == end.Id);
                        tc.Input = OpenAiProvider.ParseToolArguments(argsSb.ToString());
                        args.Remove(end.Id);
                    }
                    break;
                case StreamMessageEnd msgEnd:
                    stopReason = msgEnd.StopReason;
                    usage = msgEnd.Usage;
                    break;
            }
        }

        // Providers that never sent an end event for a call still get their arguments parsed.
        foreach (var (id, sb) in args)
            toolCalls.First(t => t.Id == id).Input = OpenAiProvider.ParseToolArguments(sb.ToString());

        return new LlmResponse
        {
            Text = text.ToString(),
            Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
            ToolCalls = toolCalls,
            StopReason = stopReason,
            Usage = usage ?? new TokenUsage()
        };
    }

    /// <summary>
    /// Run the turn's tool calls. Consecutive read-only calls (Read/Network tools) run in
    /// parallel; anything that writes, executes or asks for permission runs one at a time.
    /// Results keep the order of the calls.
    /// </summary>
    private async Task<List<MessageContent>> ExecuteToolCallsAsync(
        LlmResponse response, AgentRunOptions options, string sessionId, int turn, string cwd,
        PermissionMode permissionMode, CancellationToken ct)
    {
        var calls = response.ToolCalls;
        var results = new MessageContent[calls.Count];
        var i = 0;
        while (i < calls.Count)
        {
            var j = i;
            while (j < calls.Count && IsParallelSafe(calls[j])) j++;
            if (j - i >= 2)
            {
                var batch = Enumerable.Range(i, j - i)
                    .Select(async k => results[k] = await ExecuteOneAsync(calls[k], options, sessionId, turn, cwd, permissionMode, response.StopReason, ct));
                await Task.WhenAll(batch);
                i = j;
            }
            else
            {
                results[i] = await ExecuteOneAsync(calls[i], options, sessionId, turn, cwd, permissionMode, response.StopReason, ct);
                i++;
            }
        }
        return results.ToList();
    }

    private bool IsParallelSafe(ToolCall tc)
    {
        var tool = _toolRegistry.GetTool(tc.Name);
        return tool != null
            && tool.PermissionLevel is PermissionLevel.Read or PermissionLevel.Network
            && tool.Name != "todo"; // ordered state updates
    }

    private async Task<MessageContent> ExecuteOneAsync(
        ToolCall tc, AgentRunOptions options, string sessionId, int turn, string cwd,
        PermissionMode permissionMode, string stopReason, CancellationToken ct)
    {
        options.OnToolCall?.Invoke(tc);

        ToolExecuteResult result;
        if (tc.Input.TryGetValue(OpenAiProvider.ArgumentParseErrorKey, out var rawArgs))
        {
            var why = stopReason == "max_tokens"
                ? "The arguments were cut off by the output token limit. Split the change into smaller calls (e.g. several file_edit calls instead of one huge file_write)."
                : "The arguments were not valid JSON. Send a JSON object matching the tool's schema.";
            result = new ToolExecuteResult($"Error: could not parse arguments for tool \"{tc.Name}\". {why} Received: {rawArgs.GetString()}", true);
        }
        else
        {
            var inputElement = JsonSerializer.SerializeToElement(tc.Input);
            var hookInfo = new ToolCallInfo(tc.Id, tc.Name, inputElement);
            var preHookResult = await _hookEngine.FirePreToolUseAsync(sessionId, turn, hookInfo);

            if (preHookResult.Action == HookAction.Block)
            {
                result = new ToolExecuteResult($"[Blocked by hook] {preHookResult.Reason ?? "Tool execution blocked."}", true);
            }
            else
            {
                var effectiveInput = preHookResult.Action == HookAction.Modify && preHookResult.ModifiedInput.HasValue
                    ? preHookResult.ModifiedInput.Value
                    : inputElement;

                var execContext = new ToolExecutionContext(
                    SessionId: sessionId,
                    Cwd: cwd,
                    PlanMode: permissionMode == PermissionMode.Plan,
                    Env: new Dictionary<string, string>(),
                    Session: new Tools.SessionInfo(
                        sessionId,
                        options.Session.PermissionMode,
                        options.Session.AllowedTools.ToList(),
                        options.Session.DisallowedTools.ToList()))
                {
                    SubagentDepth = options.SubagentDepth,
                    PermissionRequest = options.OnPermissionRequest,
                    Model = options.Session.Model,
                    Provider = options.Session.Provider,
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var toolSpan = _tracer.StartSpan("tool.execute", new Dictionary<string, object>
                {
                    ["tool"] = tc.Name,
                    ["summary"] = Printer.SummarizeInput(tc.Name, effectiveInput),
                    ["input_length"] = effectiveInput.GetRawText().Length
                }))
                {
                    Func<ITool, Task<bool>>? permCallback = options.OnPermissionRequest != null
                        ? async _ => await options.OnPermissionRequest(tc)
                        : null;
                    result = await _toolRegistry.ExecuteToolAsync(tc.Name, effectiveInput, execContext, permissionMode, permCallback, ct);
                    sw.Stop();
                    _tracer.AddEvent("tool.result", new Dictionary<string, object> { ["tool"] = tc.Name, ["is_error"] = result.IsError, ["duration_ms"] = sw.ElapsedMilliseconds });
                }

                var post = await _hookEngine.FirePostToolUseAsync(sessionId, turn, hookInfo, new ToolResultInfo(result.Output, result.IsError));
                if (post.ModifiedOutput != null)
                    result = result with { Output = post.ModifiedOutput };
            }
        }

        if (options.LogToSession)
            await SafeLog(() => _sessionManager.AppendToolResultAsync(options.Session, tc.Id, result.Output, result.IsError));

        options.OnToolResult?.Invoke(new ToolResult { ToolUseId = tc.Id, Content = result.Output, IsError = result.IsError });

        await _auditLogger.LogAsync("ToolExecution", new
        {
            sessionId,
            turn,
            toolName = tc.Name,
            input = Printer.SummarizeInput(tc.Name, JsonSerializer.SerializeToElement(tc.Input)),
            isError = result.IsError,
            outputPreview = result.Output[..Math.Min(500, result.Output.Length)]
        }, sessionId);

        return new ToolResultContent
        {
            ToolUseId = tc.Id,
            Name = tc.Name,
            // Never send an empty tool result: several backends reject it.
            Content = string.IsNullOrEmpty(result.Output) ? "(no output)" : result.Output,
            IsError = result.IsError
        };
    }

    private async Task<AgentRunResult> FailAsync(
        AgentRunOptions options, string sessionId, List<Message> messages, string lastText, int turns,
        TokenUsage usage, decimal cost, string error, IProvider? provider = null, string? model = null)
    {
        var note = $"[Error: {error}]";
        options.OnText?.Invoke("\n" + note + "\n");
        // Keep user/assistant alternation so the conversation can continue after an error.
        if (messages.Count > 0 && messages[^1].Role is MessageRole.User or MessageRole.Tool)
            messages.Add(Message.FromText(MessageRole.Assistant, Join(lastText, note)));
        if (options.LogToSession)
            await SafeLog(() => _sessionManager.LogAssistantMessageAsync(options.Session, Join(lastText, note)));
        if (provider != null && model != null)
            await RecordCostAsync(options.Session, model, provider, usage, turns, cost);
        await LogSessionEnd(sessionId, turns, cost, "Error");
        return BuildResult(Join(lastText, note), turns, usage, cost, StopReason.Error, messages) with { Error = error };
    }

    private static string Join(string text, string note) =>
        string.IsNullOrWhiteSpace(text) ? note : $"{text}\n\n{note}";

    private static string Now() => DateTime.UtcNow.ToString("o");

    private static async Task SafeLog(Func<Task> log)
    {
        try { await log(); }
        catch (Exception ex) { Console.Error.WriteLine($"[session] could not write session log: {ex.Message}"); }
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
        try
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cost] could not record cost: {ex.Message}");
        }
    }
}
