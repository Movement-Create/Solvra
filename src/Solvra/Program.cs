using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using Solvra.CLI;
using Solvra.Config;
using Solvra.Core;
using Solvra.Memory;
using Solvra.Models;
using Solvra.Providers;
using Solvra.Scheduler;
using Solvra.Security;
using Solvra.Skills;
using Solvra.Tools;

namespace Solvra;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        // Keep quotes, backticks and non-ASCII readable in --json output.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await BuildRoot().InvokeAsync(args);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportFatal(ex);
            return 1;
        }
    }

    /// <summary>One readable line instead of a .NET stack trace (set SOLVRA_DEBUG=1 for the trace).</summary>
    private static void ReportFatal(Exception ex)
    {
        Console.Error.WriteLine($"solvra: {ex.Message}");
        if (Environment.GetEnvironmentVariable("SOLVRA_DEBUG") is "1" or "true")
            Console.Error.WriteLine(ex);
    }

    private static RootCommand BuildRoot()
    {
        var rootCommand = new RootCommand("Solvra — AI agent orchestrator");

        // --- shared options ---
        var providerOption = new Option<string?>("--provider", "LLM provider (wins over guessing from the model name)");
        providerOption.AddAlias("-p");
        var modelOption = new Option<string?>("--model", "Model to use (provider:model pins the provider)");
        modelOption.AddAlias("-m");
        var maxTurnsOption = new Option<int?>("--max-turns", "Maximum turns; 0 means unlimited (default: unlimited)");
        maxTurnsOption.AddValidator(r =>
        {
            if (r.GetValueOrDefault<int?>() is < 0)
                r.ErrorMessage = "--max-turns must be 0 (unlimited) or a positive integer.";
        });
        var jsonOption = new Option<bool>("--json", "Output as JSON");
        var autoOption = new Option<bool>("--auto", "Auto-approve all tool permissions");
        var planOption = new Option<bool>("--plan", "Plan mode: read-only tools only");
        var effortOption = new Option<string?>("--effort", "Reasoning effort (low/medium/high/xhigh; max is a legacy alias)");
        var subagentsOption = new Option<string?>("--subagents", "Subagent delegation mode (auto/off)");
        var subagentModelOption = new Option<string?>("--subagent-model", "Default model for subagents (defaults to the current model)");
        var subagentEffortOption = new Option<string?>("--subagent-effort", "Default subagent effort (defaults to the current effort)");
        effortOption.AddValidator(r =>
        {
            var value = r.GetValueOrDefault<string?>();
            if (value is not null && !EffortLevelExtensions.TryParse(value, out _))
                r.ErrorMessage = $"Invalid effort '{value}'. Expected low, medium, high, or xhigh.";
        });
        subagentEffortOption.AddValidator(r =>
        {
            var value = r.GetValueOrDefault<string?>();
            if (value is not null && !EffortLevelExtensions.TryParse(value, out _))
                r.ErrorMessage = $"Invalid subagent effort '{value}'. Expected low, medium, high, or xhigh.";
        });
        subagentsOption.AddValidator(r =>
        {
            var value = r.GetValueOrDefault<string?>();
            if (value is not null && value is not ("auto" or "off"))
                r.ErrorMessage = $"Invalid subagents mode '{value}'. Expected auto or off.";
        });
        var systemOption = new Option<string?>("--system", "System prompt (replaces the default instructions)");
        var sessionOption = new Option<string?>("--session", "Continue an existing session id (created if it does not exist)");
        var summaryOption = new Option<bool>("--summary", "Print end-of-session summary");
        var maxBudgetOption = new Option<decimal?>("--max-budget", "Max estimated USD per run (0 = no limit)");
        var cwdOption = new Option<string?>("--cwd", "Working directory for tools and project instructions");
        var reflectOption = new Option<bool?>("--reflect", "Run the post-task lesson-saving pass (default: config 'reflection')");
        var noSessionOption = new Option<bool>("--no-session", "Do not write a session file");
        var ephemeralOption = new Option<bool>("--ephemeral", "Do not persist a transcript");
        var noToolsOption = new Option<bool>("--no-tools", "Generation-only mode without tools, hooks, skills, memory or project instructions");
        var askAllOption = new Option<bool>("--ask-all", "Ask before every action except local workspace reads");
        var outputFormatOption = new Option<string?>("--output-format", "Output: text (default), json (same as --json) or stream-json (JSON events on stdout, control messages on stdin)");
        outputFormatOption.AddValidator(r =>
        {
            var value = r.GetValueOrDefault<string?>();
            if (value is not null && value is not ("text" or "json" or "stream-json"))
                r.ErrorMessage = $"Invalid output format '{value}'. Expected text, json or stream-json.";
        });
        var allowRuleOption = new Option<string[]>("--allow-rule", "With stream-json: an approval remembered for this session (edit, agent, network, read or execute:<command>); repeatable") { AllowMultipleArgumentsPerToken = false };
        allowRuleOption.AddValidator(r =>
        {
            foreach (var rule in r.GetValueOrDefault<string[]>() ?? [])
                if (StreamJsonRun.PermissionRule.Parse(rule) is null)
                    r.ErrorMessage = $"Invalid allow rule '{rule}'. Expected edit, agent, network, read or execute:<command>.";
        });
        var titleOption = new Option<string?>("--title", "Title for a new session (default: the first line of the prompt)");
        var permissionModeOption = new Option<string?>("--permission-mode", "Permission mode: default (ask before edits), ask-all, plan or auto. Overrides --auto, --ask-all and the config; --plan still wins");
        permissionModeOption.AddValidator(r =>
        {
            var value = r.GetValueOrDefault<string?>();
            if (value is not null && AgentHost.ParsePermissionMode(value) is null)
                r.ErrorMessage = $"Invalid permission mode '{value}'. Expected default, ask-all, plan or auto.";
        });
        var timeLimitOption = new Option<int?>("--time-limit-seconds", "Hard elapsed-time limit for this run (tools and subagents inherit it)");
        var modelTimeoutOption = new Option<int?>("--model-timeout-seconds", "Timeout for one model request; 0 disables it (default from config, 1800)");

        // --- solvra run <prompt> ---
        var runPromptArg = new Argument<string>("prompt", "The prompt to execute (use - to read it from stdin)");
        var runCommand = new Command("run", "Run agent with a prompt") { runPromptArg };
        foreach (var o in new Option[] { providerOption, modelOption, maxTurnsOption, jsonOption, autoOption, planOption, effortOption,
                     subagentsOption, subagentModelOption, subagentEffortOption, systemOption, sessionOption, summaryOption, maxBudgetOption, cwdOption, reflectOption, noSessionOption, ephemeralOption, noToolsOption, askAllOption, timeLimitOption, modelTimeoutOption, permissionModeOption, outputFormatOption, allowRuleOption, titleOption })
            runCommand.AddOption(o);

        runCommand.SetHandler(async (context) =>
        {
            var p = context.ParseResult;
            var prompt = p.GetValueForArgument(runPromptArg);
            var outputFormat = p.GetValueForOption(outputFormatOption) ?? (p.GetValueForOption(jsonOption) ? "json" : "text");
            var outputJson = outputFormat == "json";
            var streamJson = outputFormat == "stream-json";
            var auto = p.GetValueForOption(autoOption);
            var plan = p.GetValueForOption(planOption);
            var askAll = p.GetValueForOption(askAllOption);
            var ephemeral = p.GetValueForOption(ephemeralOption) || p.GetValueForOption(noSessionOption);
            var ct = context.GetCancellationToken();

            if (streamJson && prompt == "-")
            {
                Console.Error.WriteLine("solvra: with --output-format stream-json, stdin carries control messages; pass the prompt as an argument.");
                context.ExitCode = 1;
                return;
            }
            if (prompt == "-") prompt = await Console.In.ReadToEndAsync(ct);
            if (!ApplyCwd(p.GetValueForOption(cwdOption))) { context.ExitCode = 1; return; }

            var config = await ConfigLoader.LoadAsync();
            if (p.GetValueForOption(reflectOption) is bool reflect) config = config with { Reflection = reflect };
            if (p.GetValueForOption(modelTimeoutOption) is int modelTimeout) config = config with { ModelTimeoutSeconds = modelTimeout };
            var permissionMode = AgentHost.ResolvePermissionMode(p.GetValueForOption(permissionModeOption), plan, auto, askAll, config.PermissionMode);
            plan = permissionMode == "plan";
            auto = permissionMode == "auto";
            config = ApplySubagentOptions(config, p.GetValueForOption(subagentsOption), p.GetValueForOption(subagentModelOption), p.GetValueForOption(subagentEffortOption));
            var effort = p.GetValueForOption(effortOption) is { } e ? EffortLevelExtensions.Parse(e) : config.ParsedEffort;
            var (provider, model) = AgentHost.ResolveTarget(config, p.GetValueForOption(providerOption), p.GetValueForOption(modelOption), effort);

            var s = AgentHost.Build(config);
            var sessionMgr = new SessionManager(config.SessionsDir);
            var sessionConfig = new SessionConfig
            {
                Id = Guid.NewGuid().ToString(),
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Title = SessionTitles.FromText(p.GetValueForOption(titleOption)) ?? SessionTitles.FromText(prompt),
                Model = model,
                Provider = provider,
                PermissionMode = permissionMode,
                Effort = effort,
                MaxTurns = p.GetValueForOption(maxTurnsOption) ?? config.MaxTurns,
                MaxBudgetUsd = p.GetValueForOption(maxBudgetOption) ?? config.MaxBudgetUsd,
                MaxTokens = config.MaxTokens,
                SystemPrompt = p.GetValueForOption(systemOption) ?? config.SystemPrompt,
                AllowedTools = config.AllowedTools,
                DisallowedTools = config.DisallowedTools
            };

            var history = new List<Message>();
            var sessionId = p.GetValueForOption(sessionOption);
            if (!string.IsNullOrEmpty(sessionId))
            {
                if (!SessionManager.IsValidSessionId(sessionId))
                {
                    Console.Error.WriteLine($"solvra: invalid session id '{sessionId}' (letters, digits, '.', '_' and '-' only)");
                    context.ExitCode = 1;
                    return;
                }
                try
                {
                    var info = await sessionMgr.ResumeAsync(sessionId);
                    history.AddRange(info.Messages);
                    // Keep the stored session's identity and file; CLI flags for this run still apply.
                    sessionConfig = sessionConfig with { Id = info.Config.Id, CreatedAt = info.Config.CreatedAt, FilePath = info.Config.FilePath, Title = info.Config.Title };
                }
                catch (FileNotFoundException)
                {
                    sessionConfig = ephemeral
                        ? sessionConfig with { Id = sessionId }
                        : await sessionMgr.CreateAsync(sessionConfig with { Id = sessionId });
                }
            }
            else if (!ephemeral)
            {
                sessionConfig = await sessionMgr.CreateAsync(sessionConfig);
            }
            if (ephemeral) sessionConfig = sessionConfig with { FilePath = "" };

            if (streamJson)
            {
                var rules = (p.GetValueForOption(allowRuleOption) ?? []).Select(StreamJsonRun.PermissionRule.Parse).OfType<StreamJsonRun.PermissionRule>();
                context.ExitCode = await RunStreamJsonAsync(s, sessionMgr, sessionConfig, history, prompt, permissionMode, ephemeral, rules,
                    p.GetValueForOption(timeLimitOption) is > 0 and var limit ? TimeSpan.FromSeconds(limit) : null,
                    p.GetValueForOption(noToolsOption), ct);
                return;
            }

            var canPrompt = !Console.IsInputRedirected && prompt != "-";
            var approver = auto || !canPrompt ? null : (Func<ToolCall, Task<bool>>)AgentHost.AskOnConsole;
            if (!canPrompt && permissionMode is not ("auto" or "bypasspermissions" or "plan"))
                Console.Error.WriteLine("[warning] No terminal to ask for approval: write/exec/agent tools will be refused. Use --auto to allow them.");
            // Without an approver, say on stderr which tools were refused so callers such as the
            // VS Code extension can explain it; stdout (text or --json) is unchanged.
            var (onRefusalCall, onRefusalResult) = approver is null
                ? AgentHost.ApprovalNeededReporter(Console.Error)
                : ((Action<ToolCall>?)null, (Action<ToolResult>?)null);

            var result = await s.CreateReflection().RunAgentWithReflectionAsync(new AgentRunOptions
            {
                Prompt = prompt,
                Session = sessionConfig,
                History = history,
                SystemPrompt = null,
                Streaming = !outputJson,
                OnText = outputJson ? null : text => Console.Write(text),
                OnPermissionRequest = approver,
                OnToolCall = onRefusalCall,
                OnToolResult = onRefusalResult,
                SubagentsEnabled = config.SubagentsEnabled,
                SubagentModel = config.SubagentModel,
                SubagentEffort = config.ParsedSubagentEffort,
                TimeLimit = p.GetValueForOption(timeLimitOption) is > 0 and var seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : null,
                LogToSession = !ephemeral,
                NoTools = p.GetValueForOption(noToolsOption),
            }, ct);

            if (!ephemeral) await sessionMgr.LogResultAsync(sessionConfig, result);

            if (outputJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    text = result.Text,
                    turns = result.Turns,
                    cost_usd = result.CostUsd,
                    stop_reason = result.StopReason.ToString().ToLowerInvariant(),
                    error = result.Error,
                    session_id = string.IsNullOrEmpty(sessionConfig.FilePath) ? null : sessionConfig.Id,
                    model = sessionConfig.Model,
                    provider = sessionConfig.Provider,
                    effort = sessionConfig.Effort.ToWireString(),
                    subagents = config.Subagents,
                    subagent_model = config.SubagentModel,
                    subagent_effort = (config.ParsedSubagentEffort ?? sessionConfig.Effort).ToWireString(),
                    usage = new { input = result.Usage.InputTokens, output = result.Usage.OutputTokens }
                }, JsonOut));
            }
            else
            {
                Console.WriteLine();
            }

            if (p.GetValueForOption(summaryOption))
            {
                Console.WriteLine($"\n--- Summary ---");
                Console.WriteLine($"Model: {sessionConfig.Model} ({sessionConfig.Provider}), effort {sessionConfig.Effort.ToWireString()}");
                Console.WriteLine($"Subagents: {config.Subagents}; default model {config.SubagentModel ?? "inherit"}; effort {(config.ParsedSubagentEffort?.ToWireString() ?? "inherit")}");
                Console.WriteLine($"Turns: {result.Turns}");
                Console.WriteLine($"Tokens: {result.Usage.InputTokens} in / {result.Usage.OutputTokens} out");
                Console.WriteLine($"Cost: ${result.CostUsd:F4}{(result.CostUsd == 0 ? (sessionConfig.Provider == "chatgpt" ? " (covered by the ChatGPT subscription)" : " (no per-token price known for this model)") : "")}");
                Console.WriteLine($"Stop reason: {result.StopReason}");
                if (!string.IsNullOrEmpty(sessionConfig.FilePath)) Console.WriteLine($"Session: {sessionConfig.Id}");
            }

            context.ExitCode = AgentHost.ExitCode(result.StopReason);
        });

        // --- solvra chat ---
        var chatCommand = new Command("chat", "Interactive chat REPL");
        foreach (var o in new Option[] { providerOption, modelOption, effortOption, subagentsOption, subagentModelOption, subagentEffortOption, maxTurnsOption, autoOption, planOption, askAllOption, noToolsOption, ephemeralOption, systemOption, cwdOption, reflectOption, modelTimeoutOption })
            chatCommand.AddOption(o);
        var chatBudgetOption = new Option<decimal?>("--max-budget", "Max estimated USD per turn (0 = no limit)");
        chatCommand.AddOption(chatBudgetOption);
        var resumeOption = new Option<string?>("--resume", "Resume existing session");
        chatCommand.AddOption(resumeOption);
        var ndjsonOption = new Option<bool>("--ndjson", "Machine-readable NDJSON protocol on stdin/stdout");
        chatCommand.AddOption(ndjsonOption);

        chatCommand.SetHandler(async (context) =>
        {
            var p = context.ParseResult;
            var provider = p.GetValueForOption(providerOption);
            var model = p.GetValueForOption(modelOption);
            var auto = p.GetValueForOption(autoOption);
            var plan = p.GetValueForOption(planOption);
            var askAll = p.GetValueForOption(askAllOption);
            var noTools = p.GetValueForOption(noToolsOption);
            var ephemeral = p.GetValueForOption(ephemeralOption);
            var resume = p.GetValueForOption(resumeOption);
            var ndjson = p.GetValueForOption(ndjsonOption);
            var ct = context.GetCancellationToken();

            if (!ApplyCwd(p.GetValueForOption(cwdOption))) { context.ExitCode = 1; return; }

            var config = await ConfigLoader.LoadAsync();
            if (p.GetValueForOption(reflectOption) is bool reflect) config = config with { Reflection = reflect };
            if (p.GetValueForOption(modelTimeoutOption) is int modelTimeout) config = config with { ModelTimeoutSeconds = modelTimeout };
            config = ApplySubagentOptions(config, p.GetValueForOption(subagentsOption), p.GetValueForOption(subagentModelOption), p.GetValueForOption(subagentEffortOption));
            var effort = p.GetValueForOption(effortOption) is { } e ? EffortLevelExtensions.Parse(e) : config.ParsedEffort;
            var s = AgentHost.Build(config);
            var sessionMgr = new SessionManager(config.SessionsDir);

            var history = new List<Message>();
            SessionConfig sessionConfig;
            var mode = plan ? "plan" : (auto ? "auto" : askAll ? "askall" : config.PermissionMode);

            if (!string.IsNullOrEmpty(resume))
            {
                Solvra.Core.SessionInfo info;
                try { info = await sessionMgr.ResumeAsync(resume); }
                catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
                {
                    Console.Error.WriteLine($"solvra: {ex.Message}");
                    context.ExitCode = 1;
                    return;
                }
                sessionConfig = info.Config with { PermissionMode = mode, AllowedTools = config.AllowedTools, DisallowedTools = config.DisallowedTools };
                history.AddRange(info.Messages);
                if (!string.IsNullOrEmpty(model))
                {
                    // UI clients restart the process to resume; honour the model they have selected.
                    sessionConfig = sessionConfig with
                    {
                        Model = model,
                        Provider = ModelRouter.ChooseProvider(model, provider, sessionConfig.Provider, configuredIsExplicit: true),
                    };
                }
                if (!ndjson) Console.WriteLine($"Resumed session {resume} ({history.Count} messages)");
            }
            else
            {
                var (chatProvider, chatModel) = AgentHost.ResolveTarget(config, provider, model, effort);
                sessionConfig = new SessionConfig
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    Model = chatModel,
                    Provider = chatProvider,
                    PermissionMode = mode,
                    Effort = effort,
                    MaxTurns = p.GetValueForOption(maxTurnsOption) ?? config.MaxTurns,
                    MaxBudgetUsd = p.GetValueForOption(chatBudgetOption) ?? config.MaxBudgetUsd,
                    MaxTokens = config.MaxTokens,
                    SystemPrompt = p.GetValueForOption(systemOption) ?? config.SystemPrompt,
                    AllowedTools = config.AllowedTools,
                    DisallowedTools = config.DisallowedTools,
                };
                if (!ephemeral) sessionConfig = await sessionMgr.CreateAsync(sessionConfig);
            }
            if (ephemeral) sessionConfig = sessionConfig with { FilePath = "" };

            if (ndjson)
            {
                await NdjsonChat.RunAsync(s.CreateReflection(), sessionMgr, sessionConfig, history, auto, !string.IsNullOrEmpty(resume), noTools, ephemeral, ct);
                return;
            }

            await new ChatRepl(s, sessionConfig, history, auto, noTools, ephemeral).RunAsync(ct);
        });

        // --- solvra models ---
        var modelsCommand = new Command("models", "List available models");
        var modelsJsonOption = new Option<bool>("--json", "Output as JSON");
        var modelsProviderOption = new Option<string?>("--provider", "Limit to one provider");
        modelsCommand.AddOption(modelsJsonOption);
        modelsCommand.AddOption(modelsProviderOption);
        modelsCommand.SetHandler(async (context) =>
        {
            var asJson = context.ParseResult.GetValueForOption(modelsJsonOption);
            var providerFilter = context.ParseResult.GetValueForOption(modelsProviderOption);
            var config = await ConfigLoader.LoadAsync();
            var router = new ModelRouter(config.ModelTimeoutSeconds);

            if (asJson)
            {
                var providerId = providerFilter ?? config.Provider;
                var prov = router.GetProvider(providerId);
                var models = await prov.ListModelsAsync(context.GetCancellationToken());
                var descriptors = models.Select(m =>
                {
                    var descriptor = ModelCapabilities.Describe(providerId, m);
                    return descriptor with { Id = $"{providerId}:{descriptor.Id}" };
                });
                var defaultModel = providerFilter == null || providerFilter == config.Provider
                    ? (config.Model.StartsWith(providerId + ":", StringComparison.OrdinalIgnoreCase) ? config.Model : $"{providerId}:{config.Model}")
                    : null;
                var json = JsonSerializer.Serialize(new { provider = providerId, defaultModel, models = descriptors },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                Console.WriteLine(json);
                return;
            }

            var ids = providerFilter != null ? [providerFilter] : router.GetRegisteredProviderIds();
            foreach (var providerId in ids)
            {
                try
                {
                    var prov = router.GetProvider(providerId);
                    var models = await prov.ListModelsAsync(context.GetCancellationToken());
                    Console.WriteLine($"\n{prov.DisplayName} ({providerId}):");
                    foreach (var m in models)
                        Console.WriteLine($"  {m}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n{providerId}: Error — {ex.Message}");
                }
            }
            Console.WriteLine($"\nDefault: {config.Model} ({config.Provider}). Use -m provider:model to pick a provider explicitly.");
        });

        // --- solvra serve ---
        var serveCommand = new Command("serve", "Start webhook server");
        var portOption = new Option<int>("--port", () => 7331, "Webhook port");
        var hostOption = new Option<string>("--host", () => "127.0.0.1", "Address to bind (use + or 0.0.0.0 for all interfaces)");
        var noCronOption = new Option<bool>("--no-cron", "Disable cron scheduler");
        var noWebhookOption = new Option<bool>("--no-webhook", "Disable webhook server");
        var insecureOption = new Option<bool>("--insecure-no-auth", "Allow the webhook to run without SOLVRA_WEBHOOK_SECRET (anyone who can reach it can run commands)");
        serveCommand.AddOption(portOption);
        serveCommand.AddOption(hostOption);
        serveCommand.AddOption(noCronOption);
        serveCommand.AddOption(noWebhookOption);
        serveCommand.AddOption(insecureOption);

        serveCommand.SetHandler(async (context) =>
        {
            var p = context.ParseResult;
            var port = p.GetValueForOption(portOption);
            var host = p.GetValueForOption(hostOption)!;
            var noCron = p.GetValueForOption(noCronOption);
            var noWebhook = p.GetValueForOption(noWebhookOption);
            var ct = context.GetCancellationToken();

            var config = await ConfigLoader.LoadAsync();
            var s = AgentHost.Build(config);

            Func<string, string, string?, CancellationToken, Task<(string Text, int Turns)>> runAgent = async (prompt, title, modelOverride, innerCt) =>
            {
                var model = modelOverride ?? config.Model;
                var session = await new SessionManager(config.SessionsDir).CreateAsync(new SessionConfig
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    Model = model,
                    Provider = ModelRouter.ChooseProvider(model, null, config.Provider, config.ProviderIsExplicit),
                    Effort = config.ParsedEffort,
                    PermissionMode = "auto",
                    MaxTurns = config.MaxTurns,
                    MaxBudgetUsd = config.MaxBudgetUsd,
                    MaxTokens = config.MaxTokens,
                    SystemPrompt = config.SystemPrompt,
                    AllowedTools = config.AllowedTools,
                    DisallowedTools = config.DisallowedTools,
                    Title = title
                });
                var result = await s.CreateReflection().RunAgentWithReflectionAsync(new AgentRunOptions
                {
                    Prompt = prompt,
                    Session = session,
                    Streaming = false,
                    SubagentsEnabled = config.SubagentsEnabled,
                    SubagentModel = config.SubagentModel,
                    SubagentEffort = config.ParsedSubagentEffort
                }, innerCt);
                return (result.Text ?? "", result.Turns);
            };

            var tasks = new List<Task>();

            if (!noWebhook)
            {
                var secret = config.WebhookSecret ?? Environment.GetEnvironmentVariable("SOLVRA_WEBHOOK_SECRET");
                if (string.IsNullOrEmpty(secret) && !p.GetValueForOption(insecureOption))
                {
                    Console.Error.WriteLine("solvra: refusing to start the webhook without SOLVRA_WEBHOOK_SECRET (or webhook_secret in config): " +
                        "it runs agents with full tool access. Set a secret, use --no-webhook, or pass --insecure-no-auth.");
                    context.ExitCode = 1;
                    return;
                }
                var webhookServer = new WebhookServer(port, secret ?? "", host);
                webhookServer.RunAgentDelegate = (prompt, title, innerCt) => runAgent(prompt, title, null, innerCt);
                webhookServer.Start();
                Console.WriteLine($"  Webhook: POST http://{(host is "+" or "*" or "0.0.0.0" ? "localhost" : host)}:{port}/trigger");
            }

            var cronScheduler = new CronScheduler();
            if (!noCron)
            {
                cronScheduler.RunAgentDelegate = async (prompt, title, innerCt) =>
                {
                    var (text, _) = await runAgent(prompt, title, null, innerCt);
                    return text;
                };

                foreach (var job in config.Cron.Where(j => j.Enabled))
                    cronScheduler.AddJob(job);

                Console.WriteLine($"  Cron jobs: {config.Cron.Count(j => j.Enabled)} configured");
                tasks.Add(cronScheduler.StartAsync(ct));
            }

            Console.WriteLine($"Solvra server running (model {config.Model}, provider {config.Provider})");

            try
            {
                if (tasks.Count > 0)
                    await Task.WhenAll(tasks);
                else
                    await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) { /* shutdown */ }
        });

        // --- solvra memory prune ---
        var memoryCommand = new Command("memory", "Memory management");
        var pruneCommand = new Command("prune", "Prune stale lessons");
        pruneCommand.SetHandler(async () =>
        {
            var config = await ConfigLoader.LoadAsync();
            var mm = new MemoryManager(config.MemoryDir);
            var lessons = await mm.ParseLessonsAsync();
            var cutoff = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
            var oldLessons = lessons.Where(l => string.Compare(l.Date, cutoff, StringComparison.Ordinal) < 0).ToList();

            if (oldLessons.Count == 0)
            {
                Console.WriteLine("No lessons older than 30 days found.");
                return;
            }

            Console.WriteLine($"Found {oldLessons.Count} lessons older than 30 days.");
            Console.Write("Prune them? [y/N] ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer != "y" && answer != "yes") { Console.WriteLine("Cancelled."); return; }

            var remaining = lessons.Except(oldLessons).ToList();
            await mm.WriteLessonsAsync(remaining);
            Console.WriteLine($"Pruned {oldLessons.Count} lessons. {remaining.Count} remaining.");
        });
        memoryCommand.AddCommand(pruneCommand);

        // --- solvra memory add <content> ---
        var memoryAddCmd = new Command("add", "Add a fact to memory");
        var memoryAddArg = new Argument<string>("content", "The fact to remember");
        memoryAddCmd.AddArgument(memoryAddArg);
        memoryAddCmd.SetHandler(async (string content) =>
        {
            var config = await ConfigLoader.LoadAsync();
            var mm = new MemoryManager(config.MemoryDir);
            await mm.AppendFactAsync(content);
            Console.WriteLine("Fact added to memory.");
        }, memoryAddArg);
        memoryCommand.AddCommand(memoryAddCmd);

        // --- solvra memory search <query> ---
        var memorySearchCmd = new Command("search", "Search memory");
        var memorySearchArg = new Argument<string>("query", "Search query");
        memorySearchCmd.AddArgument(memorySearchArg);
        memorySearchCmd.SetHandler(async (string query) =>
        {
            var config = await ConfigLoader.LoadAsync();
            var mm = new MemoryManager(config.MemoryDir);
            var results = await mm.SearchAsync(query);
            if (string.IsNullOrWhiteSpace(results))
                Console.WriteLine("No results found.");
            else
                Console.WriteLine(results);
        }, memorySearchArg);
        memoryCommand.AddCommand(memorySearchCmd);

        // --- solvra session list / show ---
        var sessionCommand = new Command("session", "Session management");
        var sessionListCommand = new Command("list", "List sessions (newest activity first)");
        var sessionListJsonOption = new Option<bool>("--json", "Output as a JSON array (id, title, created_at, updated_at, model, provider)");
        sessionListCommand.AddOption(sessionListJsonOption);
        sessionListCommand.SetHandler(async (bool asJson) =>
        {
            var config = await ConfigLoader.LoadAsync();
            var mgr = new SessionManager(config.SessionsDir);
            var sessions = await mgr.ListSummariesAsync();

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(sessions, StreamJsonRun.Json));
                return;
            }
            if (sessions.Count == 0)
            {
                Console.WriteLine("No sessions found.");
                return;
            }

            foreach (var s in sessions)
            {
                Console.WriteLine($"  {s.Id}  {s.CreatedAt}  {s.Model}  {s.Title}");
            }
        }, sessionListJsonOption);

        var sessionShowArg = new Argument<string>("id", "Session ID to show");
        var sessionShowCommand = new Command("show", "Show session details") { sessionShowArg };
        sessionShowCommand.SetHandler(async (id) =>
        {
            var config = await ConfigLoader.LoadAsync();
            var mgr = new SessionManager(config.SessionsDir);
            try
            {
                var info = await mgr.ResumeAsync(id);
                Console.WriteLine($"Session: {info.Config.Id}");
                Console.WriteLine($"Model: {info.Config.Model}");
                Console.WriteLine($"Created: {info.Config.CreatedAt}");
                Console.WriteLine($"Messages: {info.Messages.Count}");
                Console.WriteLine();
                foreach (var msg in info.Messages)
                {
                    Console.WriteLine($"[{msg.Role}] {msg.GetTextContent()}");
                }
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine($"Session not found: {id}");
            }
        }, sessionShowArg);


        // --- solvra session resume <id> ---
        var sessionResumeArg = new Argument<string>("id", "Session ID to resume");
        var sessionResumeCmd = new Command("resume", "Resume a previous session") { sessionResumeArg };
        sessionResumeCmd.AddOption(autoOption);
        sessionResumeCmd.AddOption(planOption);
        sessionResumeCmd.SetHandler(async (context) =>
        {
            var id = context.ParseResult.GetValueForArgument(sessionResumeArg);
            var auto = context.ParseResult.GetValueForOption(autoOption);
            var plan = context.ParseResult.GetValueForOption(planOption);
            var ct = context.GetCancellationToken();

            var config = await ConfigLoader.LoadAsync();
            var sm = new SessionManager(config.SessionsDir);
            Solvra.Core.SessionInfo info;
            try { info = await sm.ResumeAsync(id); }
            catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
            {
                Console.Error.WriteLine($"solvra: {ex.Message}");
                context.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Resumed session {id} with {info.Messages.Count} messages.");
            var s = AgentHost.Build(config);
            var mode = plan ? "plan" : (auto ? "auto" : config.PermissionMode);
            var session = info.Config with { PermissionMode = mode, AllowedTools = config.AllowedTools, DisallowedTools = config.DisallowedTools };
            await new ChatRepl(s, session, new List<Message>(info.Messages), auto).RunAsync(ct);
        });

        // --- solvra session delete <id> ---
        var sessionDeleteArg = new Argument<string>("id", "Session ID to delete");
        var sessionDeleteCmd = new Command("delete", "Delete a session") { sessionDeleteArg };
        sessionDeleteCmd.SetHandler(async (context) =>
        {
            var id = context.ParseResult.GetValueForArgument(sessionDeleteArg);
            if (!SessionManager.IsValidSessionId(id))
            {
                Console.Error.WriteLine($"solvra: invalid session id '{id}'");
                context.ExitCode = 1;
                return;
            }
            var config = await ConfigLoader.LoadAsync();
            var sm = new SessionManager(config.SessionsDir);
            var filePath = Path.Combine(config.SessionsDir, $"{id}.jsonl");
            if (File.Exists(filePath))
            {
                await sm.DeleteAsync(id);
                Console.WriteLine($"Session {id} deleted.");
            }
            else
            {
                Console.WriteLine($"Session {id} not found.");
                context.ExitCode = 1;
            }
        });

        sessionCommand.AddCommand(sessionListCommand);
        sessionCommand.AddCommand(sessionShowCommand);
        sessionCommand.AddCommand(sessionResumeCmd);
        sessionCommand.AddCommand(sessionDeleteCmd);

        // --- solvra session rename <id> <title> ---
        var sessionRenameIdArg = new Argument<string>("id", "Session ID to rename");
        var sessionRenameTitleArg = new Argument<string>("title", "New title");
        var sessionRenameCmd = new Command("rename", "Rename a session (appends a title event; history is untouched)") { sessionRenameIdArg, sessionRenameTitleArg };
        sessionRenameCmd.SetHandler(async (context) =>
        {
            var id = context.ParseResult.GetValueForArgument(sessionRenameIdArg);
            var title = context.ParseResult.GetValueForArgument(sessionRenameTitleArg);
            if (!SessionManager.IsValidSessionId(id))
            {
                Console.Error.WriteLine($"solvra: invalid session id '{id}'");
                context.ExitCode = 1;
                return;
            }
            var config = await ConfigLoader.LoadAsync();
            try
            {
                await new SessionManager(config.SessionsDir).RenameAsync(id, title);
                Console.WriteLine($"Session {id} renamed.");
            }
            catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
            {
                Console.Error.WriteLine($"solvra: {ex.Message}");
                context.ExitCode = 1;
            }
        });
        sessionCommand.AddCommand(sessionRenameCmd);

        // --- solvra tools ---
        var toolsCmd = new Command("tools", "List all available tools");
        toolsCmd.SetHandler(() =>
        {
            var sandbox = new SandboxManager(new SandboxConfig());
            var registry = new ToolRegistry();
            registry.RegisterBuiltins(sandbox);
            var defs = registry.GetToolDefinitions();
            Console.WriteLine($"Available tools ({defs.Count}):\n");
            foreach (var d in defs)
                Console.WriteLine($"  {d.Name,-25} {d.Description}");
        });

        // --- solvra skills ---
        var skillsCmd = new Command("skills", "List discovered skills");
        skillsCmd.SetHandler(async () =>
        {
            var config = await ConfigLoader.LoadAsync();
            var loader = new SkillLoader(config.SkillsDir);
            var skills = await loader.GetAllSkillsAsync();
            Console.WriteLine($"Discovered skills ({skills.Count}):\n");
            foreach (var s in skills)
                Console.WriteLine($"  {s.Name,-25} {s.Description}");
            if (skills.Count == 0) Console.WriteLine("No skills found in skills/ directory.");
        });


        rootCommand.AddCommand(runCommand);
        rootCommand.AddCommand(chatCommand);
        rootCommand.AddCommand(modelsCommand);
        rootCommand.AddCommand(serveCommand);
        rootCommand.AddCommand(memoryCommand);
        rootCommand.AddCommand(sessionCommand);
        rootCommand.AddCommand(toolsCmd);
        rootCommand.AddCommand(skillsCmd);

        return rootCommand;
    }

    /// <summary>
    /// <c>run --output-format stream-json</c>: events on stdout, control messages on stdin (see
    /// <see cref="StreamJsonRun"/>). Approvals come from the control channel unless the mode is auto.
    /// </summary>
    private static async Task<int> RunStreamJsonAsync(AgentSubsystems s, SessionManager sessionMgr, SessionConfig sessionConfig,
        List<Message> history, string prompt, string permissionMode, bool ephemeral, IEnumerable<StreamJsonRun.PermissionRule> rules,
        TimeSpan? timeLimit, bool noTools, CancellationToken ct)
    {
        var config = s.Config;
        var cwd = Directory.GetCurrentDirectory();
        var stream = new StreamJsonRun(Console.Out, s.Registry, cwd, sessionConfig.Model, sessionConfig.Provider, rules)
        {
            CurrentSessionId = sessionConfig.Id,
        };
        var auto = permissionMode is "auto" or "bypasspermissions";
        stream.Emit(new
        {
            type = "session",
            id = string.IsNullOrEmpty(sessionConfig.FilePath) ? null : sessionConfig.Id,
            title = SessionTitles.Display(sessionConfig.Title, prompt),
            model = sessionConfig.Model,
            provider = sessionConfig.Provider,
            cwd,
            mode = permissionMode switch { "plan" => "plan", "auto" or "bypasspermissions" => "auto", "askall" => "ask-all", _ => "ask" },
            effort = sessionConfig.Effort.ToWireString(),
            resumed = history.Count > 0,
            rules = stream.Rules.Select(r => r.ToString()).ToArray(),
        });

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var readerCts = new CancellationTokenSource();
        var control = Task.Run(() => stream.ReadControlAsync(Console.In, runCts, readerCts.Token));

        var hooks = stream.HooksFor("main");
        var streamed = new System.Text.StringBuilder();
        var started = System.Diagnostics.Stopwatch.StartNew();
        AgentRunResult? result = null;
        var cancelled = false;
        try
        {
            result = await s.CreateReflection().RunAgentWithReflectionAsync(new AgentRunOptions
            {
                Prompt = prompt,
                Session = sessionConfig,
                History = history,
                Streaming = true,
                OnText = text => { lock (streamed) streamed.Append(text); hooks.OnText!(text); },
                OnThinking = hooks.OnThinking,
                OnToolCall = hooks.OnToolCall,
                OnToolResult = hooks.OnToolResult,
                OnUsage = hooks.OnUsage,
                OnPermissionRequest = auto ? null : hooks.OnPermissionRequest,
                Observer = stream,
                AgentId = "main",
                SubagentsEnabled = config.SubagentsEnabled,
                SubagentModel = config.SubagentModel,
                SubagentEffort = config.ParsedSubagentEffort,
                TimeLimit = timeLimit,
                LogToSession = !ephemeral,
                NoTools = noTools,
            }, runCts.Token);
            if (runCts.IsCancellationRequested && !ct.IsCancellationRequested) cancelled = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            stream.EmitError(ex.Message, StreamJsonRun.FixFor(ex.Message), sessionConfig.Provider);
        }
        finally
        {
            stream.DenyAllPending();
            readerCts.Cancel();
        }

        if (cancelled && result is null && !ephemeral)
        {
            // Keep user/assistant alternation so the session still resumes cleanly.
            string partial;
            lock (streamed) partial = streamed.ToString();
            try { await sessionMgr.LogAssistantMessageAsync(sessionConfig, string.IsNullOrWhiteSpace(partial) ? "(interrupted)" : partial + "\n\n(interrupted)"); }
            catch { /* logging must not hide the cancel */ }
        }
        if (result is not null && !ephemeral) await sessionMgr.LogResultAsync(sessionConfig, result);
        if (result is { StopReason: StopReason.Error })
            stream.EmitError(result.Error ?? result.Text, StreamJsonRun.FixFor(result.Error ?? result.Text), sessionConfig.Provider);

        var (input, output, cost) = stream.Totals();
        string finalText;
        lock (streamed) finalText = result?.Text ?? streamed.ToString();
        stream.Emit(new
        {
            type = "done",
            stop_reason = cancelled ? "cancelled" : result?.StopReason.ToString().ToLowerInvariant() ?? "error",
            text = finalText,
            error = result?.Error,
            session_id = string.IsNullOrEmpty(sessionConfig.FilePath) ? null : sessionConfig.Id,
            turns = result?.Turns ?? 0,
            duration_ms = started.ElapsedMilliseconds,
            cost_usd = Math.Max(cost, result?.CostUsd ?? 0),
            usage = new { input = Math.Max(input, result?.Usage.InputTokens ?? 0), output = Math.Max(output, result?.Usage.OutputTokens ?? 0) },
        });
        // Do not wait for stdin: the caller may keep it open. The reader stops on its own token.
        _ = control;
        return cancelled ? 130 : result is null ? 1 : AgentHost.ExitCode(result.StopReason);
    }

    private static SolvraConfig ApplySubagentOptions(SolvraConfig config, string? mode, string? model, string? effort)
    {
        var result = config with
        {
            Subagents = mode ?? config.Subagents,
            SubagentModel = model ?? config.SubagentModel,
            SubagentEffort = effort ?? config.SubagentEffort,
        };
        _ = result.SubagentsEnabled;
        _ = result.ParsedSubagentEffort;
        return result;
    }

    private static bool ApplyCwd(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return true;
        if (!Directory.Exists(cwd))
        {
            Console.Error.WriteLine($"solvra: --cwd directory not found: {cwd}");
            return false;
        }
        Directory.SetCurrentDirectory(cwd);
        return true;
    }
}
