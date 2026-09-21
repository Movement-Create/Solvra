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
        var maxTurnsOption = new Option<int?>("--max-turns", "Max turns (default from config, 50)");
        var jsonOption = new Option<bool>("--json", "Output as JSON");
        var autoOption = new Option<bool>("--auto", "Auto-approve all tool permissions");
        var planOption = new Option<bool>("--plan", "Plan mode: read-only tools only");
        var effortOption = new Option<string?>("--effort", "Effort level (low/medium/high/max)");
        var systemOption = new Option<string?>("--system", "System prompt (replaces the default instructions)");
        var sessionOption = new Option<string?>("--session", "Continue an existing session id (created if it does not exist)");
        var summaryOption = new Option<bool>("--summary", "Print end-of-session summary");
        var maxBudgetOption = new Option<decimal?>("--max-budget", "Max estimated USD per run (0 = no limit)");
        var cwdOption = new Option<string?>("--cwd", "Working directory for tools and project instructions");
        var reflectOption = new Option<bool?>("--reflect", "Run the post-task lesson-saving pass (default: config 'reflection')");
        var noSessionOption = new Option<bool>("--no-session", "Do not write a session file");

        // --- solvra run <prompt> ---
        var runPromptArg = new Argument<string>("prompt", "The prompt to execute (use - to read it from stdin)");
        var runCommand = new Command("run", "Run agent with a prompt") { runPromptArg };
        foreach (var o in new Option[] { providerOption, modelOption, maxTurnsOption, jsonOption, autoOption, planOption, effortOption,
                     systemOption, sessionOption, summaryOption, maxBudgetOption, cwdOption, reflectOption, noSessionOption })
            runCommand.AddOption(o);

        runCommand.SetHandler(async (context) =>
        {
            var p = context.ParseResult;
            var prompt = p.GetValueForArgument(runPromptArg);
            var outputJson = p.GetValueForOption(jsonOption);
            var auto = p.GetValueForOption(autoOption);
            var plan = p.GetValueForOption(planOption);
            var ct = context.GetCancellationToken();

            if (prompt == "-") prompt = await Console.In.ReadToEndAsync(ct);
            if (!ApplyCwd(p.GetValueForOption(cwdOption))) { context.ExitCode = 1; return; }

            var config = await ConfigLoader.LoadAsync();
            if (p.GetValueForOption(reflectOption) is bool reflect) config = config with { Reflection = reflect };
            var effort = p.GetValueForOption(effortOption) is { } e ? EffortLevelExtensions.Parse(e) : config.ParsedEffort;
            var (provider, model) = AgentHost.ResolveTarget(config, p.GetValueForOption(providerOption), p.GetValueForOption(modelOption), effort);

            var s = AgentHost.Build(config);
            var sessionMgr = new SessionManager(config.SessionsDir);
            var sessionConfig = new SessionConfig
            {
                Id = Guid.NewGuid().ToString(),
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Title = prompt.Length > 80 ? prompt[..80] : prompt,
                Model = model,
                Provider = provider,
                PermissionMode = plan ? "plan" : (auto ? "auto" : config.PermissionMode),
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
                    sessionConfig = await sessionMgr.CreateAsync(sessionConfig with { Id = sessionId });
                }
            }
            else if (!p.GetValueForOption(noSessionOption))
            {
                sessionConfig = await sessionMgr.CreateAsync(sessionConfig);
            }

            var canPrompt = !Console.IsInputRedirected && prompt != "-";
            if (!auto && !plan && !canPrompt && config.PermissionMode is not ("auto" or "bypasspermissions"))
                Console.Error.WriteLine("[warning] No terminal to ask for approval: write/exec/agent tools will be refused. Use --auto to allow them.");

            var result = await s.CreateReflection().RunAgentWithReflectionAsync(new AgentRunOptions
            {
                Prompt = prompt,
                Session = sessionConfig,
                History = history,
                SystemPrompt = null,
                Streaming = !outputJson,
                OnText = outputJson ? null : text => Console.Write(text),
                OnPermissionRequest = auto || !canPrompt ? null : AgentHost.AskOnConsole,
            }, ct);

            await sessionMgr.LogResultAsync(sessionConfig, result);

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
                Console.WriteLine($"Model: {sessionConfig.Model} ({sessionConfig.Provider})");
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
        foreach (var o in new Option[] { providerOption, modelOption, effortOption, maxTurnsOption, autoOption, planOption, systemOption, cwdOption, reflectOption })
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
            var resume = p.GetValueForOption(resumeOption);
            var ndjson = p.GetValueForOption(ndjsonOption);
            var ct = context.GetCancellationToken();

            if (!ApplyCwd(p.GetValueForOption(cwdOption))) { context.ExitCode = 1; return; }

            var config = await ConfigLoader.LoadAsync();
            if (p.GetValueForOption(reflectOption) is bool reflect) config = config with { Reflection = reflect };
            var effort = p.GetValueForOption(effortOption) is { } e ? EffortLevelExtensions.Parse(e) : config.ParsedEffort;
            var s = AgentHost.Build(config);
            var sessionMgr = new SessionManager(config.SessionsDir);

            var history = new List<Message>();
            SessionConfig sessionConfig;
            var mode = plan ? "plan" : (auto ? "auto" : config.PermissionMode);

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
                sessionConfig = await sessionMgr.CreateAsync(new SessionConfig
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
                });
            }

            if (ndjson)
            {
                await NdjsonChat.RunAsync(s.CreateReflection(), sessionMgr, sessionConfig, history, auto, !string.IsNullOrEmpty(resume), ct);
                return;
            }

            await new ChatRepl(s, sessionConfig, history, auto).RunAsync(ct);
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
            var router = new ModelRouter();
            var config = await ConfigLoader.LoadAsync();

            if (asJson)
            {
                var providerId = providerFilter ?? config.Provider;
                var prov = router.GetProvider(providerId);
                var models = await prov.ListModelsAsync(context.GetCancellationToken());
                var json = JsonSerializer.Serialize(new { provider = providerId, defaultModel = providerFilter == null || providerFilter == config.Provider ? config.Model : null, models },
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
                    Streaming = false
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
        var sessionListCommand = new Command("list", "List sessions");
        sessionListCommand.SetHandler(async () =>
        {
            var config = await ConfigLoader.LoadAsync();
            var mgr = new SessionManager(config.SessionsDir);
            var sessions = await mgr.ListAsync();

            if (sessions.Count == 0)
            {
                Console.WriteLine("No sessions found.");
                return;
            }

            foreach (var s in sessions)
            {
                Console.WriteLine($"  {s.Id}  {s.CreatedAt}  {s.Model}  {s.Title ?? "(untitled)"}");
            }
        });

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
