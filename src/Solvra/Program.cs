using System.CommandLine;
using System.Text.Json;
using Solvra.Config;
using Solvra.Core;
using Solvra.Hooks;
using Solvra.Memory;
using Solvra.Models;
using Solvra.Observability;
using Solvra.Providers;
using Solvra.Scheduler;
using Solvra.Security;
using Solvra.Skills;
using Solvra.Tools;

namespace Solvra;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Solvra — AI agent orchestrator");

        // --- solvra run <prompt> ---
        var runPromptArg = new Argument<string>("prompt", "The prompt to execute");
        var providerOption = new Option<string?>("--provider", "LLM provider");
        providerOption.AddAlias("-p");
        var modelOption = new Option<string?>("--model", "Model to use");
        modelOption.AddAlias("-m");
        var maxTurnsOption = new Option<int?>("--max-turns", "Max turns");
        var jsonOption = new Option<bool>("--json", "Output as JSON");
        var autoOption = new Option<bool>("--auto", "Auto-approve all tool permissions");
        var planOption = new Option<bool>("--plan", "Plan mode");
        var effortOption = new Option<string?>("--effort", "Effort level (low/medium/high/max)");
        var systemOption = new Option<string?>("--system", "System prompt override");
        var sessionOption = new Option<string?>("--session", "Session ID to resume or name");
        var summaryOption = new Option<bool>("--summary", "Print end-of-session summary");

        var runCommand = new Command("run", "Run agent with a prompt") { runPromptArg };
        runCommand.AddOption(providerOption);
        runCommand.AddOption(modelOption);
        runCommand.AddOption(maxTurnsOption);
        runCommand.AddOption(jsonOption);
        runCommand.AddOption(autoOption);
        runCommand.AddOption(planOption);
        runCommand.AddOption(effortOption);
        runCommand.AddOption(systemOption);
        runCommand.AddOption(sessionOption);
        runCommand.AddOption(summaryOption);

        runCommand.SetHandler(async (context) =>
        {
            var prompt = context.ParseResult.GetValueForArgument(runPromptArg);
            var provider = context.ParseResult.GetValueForOption(providerOption);
            var model = context.ParseResult.GetValueForOption(modelOption);
            var maxTurns = context.ParseResult.GetValueForOption(maxTurnsOption);
            var outputJson = context.ParseResult.GetValueForOption(jsonOption);
            var auto = context.ParseResult.GetValueForOption(autoOption);
            var plan = context.ParseResult.GetValueForOption(planOption);
            var effort = context.ParseResult.GetValueForOption(effortOption);
            var system = context.ParseResult.GetValueForOption(systemOption);
            var showSummary = context.ParseResult.GetValueForOption(summaryOption);

            var config = await ConfigLoader.LoadAsync();

            var sessionConfig = new SessionConfig
            {
                Id = Guid.NewGuid().ToString(),
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Model = model ?? config.Model,
                Provider = provider ?? config.Provider,
                PermissionMode = plan ? "plan" : (auto ? "auto" : config.PermissionMode),
                Effort = effort != null ? EffortLevelExtensions.Parse(effort) : config.ParsedEffort,
                MaxTurns = maxTurns ?? 20,
                MaxBudgetUsd = config.MaxBudgetUsd,
                SystemPrompt = system ?? config.SystemPrompt,
                AllowedTools = config.AllowedTools,
                DisallowedTools = config.DisallowedTools
            };

            var (router, registry, hookEngine, auditLogger, skillLoader, memoryManager, permissionChecker, tracer) =
                BuildSubsystems(config);

            // Set up AgentTool delegate
            AgentTool.RunAgentDelegate = async (agentPrompt, agentModel, agentSystem, agentMaxTurns, agentCt) =>
            {
                var subLoop = new AgentLoop(router, registry, hookEngine, auditLogger, tracer: tracer);
                var subSession = sessionConfig with
                {
                    Id = Guid.NewGuid().ToString(),
                    Model = agentModel ?? sessionConfig.Model,
                    MaxTurns = agentMaxTurns
                };
                var subResult = await subLoop.RunAsync(new AgentRunOptions
                {
                    Prompt = agentPrompt,
                    Session = subSession,
                    SystemPrompt = agentSystem,
                    Streaming = false
                }, agentCt);
                return subResult.Text ?? "";
            };

            // SB10: Headless permission warning
            if (!auto && Console.IsInputRedirected)
            {
                Console.Error.WriteLine("[Warning] Headless mode with default permissions. Execute/Agent tools will be denied. Use --auto for headless operation.");
            }

            var agentLoop = new AgentLoop(router, registry, hookEngine, auditLogger, skillLoader, memoryManager, permissionChecker, tracer: tracer);
            var reflection = new Reflection(agentLoop);

            var result = await reflection.RunAgentWithReflectionAsync(new AgentRunOptions
            {
                Prompt = prompt,
                Session = sessionConfig,
                SystemPrompt = system,
                Streaming = !outputJson,
                OnText = outputJson ? null : text => Console.Write(text),
                OnPermissionRequest = auto ? null : async tc =>
                {
                    Console.Write($"\nAllow tool '{tc.Name}'? (y/n): ");
                    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                    return answer is "y" or "yes";
                }
            }, context.GetCancellationToken());

            if (outputJson)
            {
                var json = JsonSerializer.Serialize(new
                {
                    text = result.Text,
                    turns = result.Turns,
                    cost_usd = result.CostUsd,
                    stop_reason = result.StopReason.ToString().ToLowerInvariant(),
                    usage = new { input = result.Usage.InputTokens, output = result.Usage.OutputTokens }
                }, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
            }
            else
            {
                Console.WriteLine();
            }

            if (showSummary)
            {
                Console.WriteLine($"\n--- Summary ---");
                Console.WriteLine($"Turns: {result.Turns}");
                Console.WriteLine($"Tokens: {result.Usage.InputTokens} in / {result.Usage.OutputTokens} out");
                Console.WriteLine($"Cost: ${result.CostUsd:F4}");
                Console.WriteLine($"Stop reason: {result.StopReason}");
            }
        });

        // --- solvra chat ---
        var chatCommand = new Command("chat", "Interactive chat REPL");
        chatCommand.AddOption(providerOption);
        chatCommand.AddOption(modelOption);
        chatCommand.AddOption(effortOption);
        chatCommand.AddOption(maxTurnsOption);
        chatCommand.AddOption(autoOption);
        chatCommand.AddOption(planOption);
        var maxBudgetOption = new Option<decimal?>("--max-budget", "Max USD budget per session");
        chatCommand.AddOption(maxBudgetOption);
        var resumeOption = new Option<string?>("--resume", "Resume existing session");
        chatCommand.AddOption(resumeOption);

        chatCommand.SetHandler(async (context) =>
        {
            var provider = context.ParseResult.GetValueForOption(providerOption);
            var model = context.ParseResult.GetValueForOption(modelOption);
            var maxTurns = context.ParseResult.GetValueForOption(maxTurnsOption);
            var auto = context.ParseResult.GetValueForOption(autoOption);
            var effort = context.ParseResult.GetValueForOption(effortOption);
            var maxBudget = context.ParseResult.GetValueForOption(maxBudgetOption);
            var resume = context.ParseResult.GetValueForOption(resumeOption);
            var ct = context.GetCancellationToken();

            var config = await ConfigLoader.LoadAsync();

            var (router, registry, hookEngine, auditLogger, skillLoader, memoryManager, permissionChecker, tracer) =
                BuildSubsystems(config);

            var sessionMgr = new SessionManager(config.SessionsDir);

            // Set up AgentTool delegate
            AgentTool.RunAgentDelegate = async (agentPrompt, agentModel, agentSystem, agentMaxTurns, agentCt) =>
            {
                var subLoop = new AgentLoop(router, registry, hookEngine, auditLogger, tracer: tracer);
                var subResult = await subLoop.RunAsync(new AgentRunOptions
                {
                    Prompt = agentPrompt,
                    Session = new SessionConfig
                    {
                        Id = Guid.NewGuid().ToString(),
                        CreatedAt = DateTime.UtcNow.ToString("o"),
                        Model = agentModel ?? config.Model,
                        MaxTurns = agentMaxTurns
                    },
                    SystemPrompt = agentSystem,
                    Streaming = false
                }, agentCt);
                return subResult.Text ?? "";
            };

            var agentLoop = new AgentLoop(router, registry, hookEngine, auditLogger, skillLoader, memoryManager, permissionChecker, tracer: tracer);
            var reflection = new Reflection(agentLoop);

            var history = new List<Message>();
            SessionConfig sessionConfig;

            if (!string.IsNullOrEmpty(resume))
            {
                var info = await sessionMgr.ResumeAsync(resume);
                sessionConfig = info.Config;
                history.AddRange(info.Messages);
                Console.WriteLine($"Resumed session {resume}");
            }
            else
            {
                sessionConfig = await sessionMgr.CreateAsync(new SessionConfig
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    Model = model ?? config.Model,
                    Provider = provider ?? config.Provider,
                    PermissionMode = auto ? "auto" : config.PermissionMode,
                    Effort = effort != null ? EffortLevelExtensions.Parse(effort) : config.ParsedEffort,
                    MaxTurns = maxTurns ?? config.MaxTurns,
                    MaxBudgetUsd = maxBudget ?? 5.0m,
                });
            }

            Console.WriteLine($"Solvra Chat ({sessionConfig.Model}) — type /exit to quit");

            while (!ct.IsCancellationRequested)
            {
                Console.Write("\nyou> ");
                var input = Console.ReadLine();
                if (input == null) break;
                input = input.Trim();

                switch (input.ToLowerInvariant())
                {
                    case "/exit" or "/quit" or "/q":
                        Console.WriteLine("Goodbye!");
                        return;
                    case "/help":
                        Console.WriteLine("Commands: /exit, /quit, /q, /help, /session, /tools");
                        continue;
                    case "/session":
                        Console.WriteLine($"Session: {sessionConfig.Id}");
                        Console.WriteLine($"Model: {sessionConfig.Model}");
                        Console.WriteLine($"Turns: {history.Count(m => m.Role == MessageRole.User)}");
                        continue;
                    case "/tools":
                        foreach (var tool in registry.GetToolDefinitions())
                            Console.WriteLine($"  {tool.Name}: {tool.Description}");
                        continue;
                    case "":
                        continue;
                }

                var result = await reflection.RunAgentWithReflectionAsync(new AgentRunOptions
                {
                    Prompt = input,
                    Session = sessionConfig,
                    History = history,
                    Streaming = true,
                    OnText = text => Console.Write(text),
                    OnPermissionRequest = auto ? null : async tc =>
                    {
                        Console.Write($"\nAllow tool '{tc.Name}'? (y/n): ");
                        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                        return answer is "y" or "yes";
                    }
                }, ct);

                history = [..result.Messages];
                Console.WriteLine();

                await sessionMgr.LogUserMessageAsync(sessionConfig, input);
                await sessionMgr.LogAssistantMessageAsync(sessionConfig, result.Text);
            }
        });

        // --- solvra models ---
        var modelsCommand = new Command("models", "List available models");
        modelsCommand.SetHandler(async (context) =>
        {
            var router = new ModelRouter();
            foreach (var providerId in router.GetRegisteredProviderIds())
            {
                try
                {
                    var prov = router.GetProvider(providerId);
                    var models = await prov.ListModelsAsync(context.GetCancellationToken());
                    Console.WriteLine($"\n{prov.DisplayName}:");
                    foreach (var m in models)
                        Console.WriteLine($"  {m}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n{providerId}: Error — {ex.Message}");
                }
            }
        });

        // --- solvra serve ---
        var serveCommand = new Command("serve", "Start webhook server");
        var portOption = new Option<int>("--port", () => 7331, "Webhook port");
        var noCronOption = new Option<bool>("--no-cron", "Disable cron scheduler");
        var noWebhookOption = new Option<bool>("--no-webhook", "Disable webhook server");
        serveCommand.AddOption(portOption);
        serveCommand.AddOption(noCronOption);
        serveCommand.AddOption(noWebhookOption);

        serveCommand.SetHandler(async (context) =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var noCron = context.ParseResult.GetValueForOption(noCronOption);
            var noWebhook = context.ParseResult.GetValueForOption(noWebhookOption);
            var ct = context.GetCancellationToken();

            var config = await ConfigLoader.LoadAsync();
            var (router, registry, hookEngine, auditLogger, skillLoader, memoryManager, permissionChecker, tracer) =
                BuildSubsystems(config);

            // Build an agent-runner delegate for webhook/cron to invoke
            Func<string, string, CancellationToken, Task<(string Text, int Turns)>> runAgent = async (prompt, title, innerCt) =>
            {
                var loop = new AgentLoop(router, registry, hookEngine, auditLogger, skillLoader, memoryManager, permissionChecker, tracer: tracer);
                var session = new SessionConfig
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    Model = config.Model,
                    Provider = config.Provider,
                    PermissionMode = "auto",
                    MaxTurns = config.MaxTurns,
                    MaxBudgetUsd = config.MaxBudgetUsd,
                    Title = title
                };
                var result = await loop.RunAsync(new AgentRunOptions
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
                var webhookServer = new WebhookServer(port, config.WebhookSecret);
                webhookServer.RunAgentDelegate = runAgent;
                webhookServer.Start();
                Console.WriteLine($"  Webhook: POST http://localhost:{port}/trigger");
            }

            var cronScheduler = new CronScheduler();
            if (!noCron)
            {
                cronScheduler.RunAgentDelegate = async (prompt, title, innerCt) =>
                {
                    var (text, _) = await runAgent(prompt, title, innerCt);
                    return text;
                };

                foreach (var job in config.Cron)
                    cronScheduler.AddJob(job);

                Console.WriteLine($"  Cron jobs: {config.Cron.Count} configured");
                tasks.Add(cronScheduler.StartAsync(ct));
            }

            Console.WriteLine($"Solvra server listening on port {port}");

            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
            else
                await Task.Delay(Timeout.Infinite, ct);
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
        sessionResumeCmd.SetHandler(async (context) =>
        {
            var id = context.ParseResult.GetValueForArgument(sessionResumeArg);
            var auto = context.ParseResult.GetValueForOption(autoOption);
            var ct = context.GetCancellationToken();

            var config = await ConfigLoader.LoadAsync();
            var sm = new SessionManager(config.SessionsDir);
            try
            {
                var info = await sm.ResumeAsync(id);
                Console.WriteLine($"Resumed session {id} with {info.Messages.Count} messages.");

                // SB7: Drop into interactive chat REPL with the resumed session
                var (router, registry, hookEngine, auditLogger, skillLoader, memoryManager, permissionChecker, tracer) =
                    BuildSubsystems(config);

                var agentLoop = new AgentLoop(router, registry, hookEngine, auditLogger, skillLoader, memoryManager, permissionChecker, tracer: tracer);
                var reflection = new Reflection(agentLoop);
                var sessionConfig = info.Config;
                var history = new List<Message>(info.Messages);

                Console.WriteLine($"Solvra Chat ({sessionConfig.Model}) — type /exit to quit");

                while (!ct.IsCancellationRequested)
                {
                    Console.Write("\nyou> ");
                    var input = Console.ReadLine();
                    if (input == null) break;
                    input = input.Trim();

                    switch (input.ToLowerInvariant())
                    {
                        case "/exit" or "/quit" or "/q":
                            Console.WriteLine("Goodbye!");
                            return;
                        case "/help":
                            Console.WriteLine("Commands: /exit, /quit, /q, /help, /session, /tools");
                            continue;
                        case "/session":
                            Console.WriteLine($"Session: {sessionConfig.Id}");
                            Console.WriteLine($"Model: {sessionConfig.Model}");
                            Console.WriteLine($"Turns: {history.Count(m => m.Role == MessageRole.User)}");
                            continue;
                        case "/tools":
                            foreach (var tool in registry.GetToolDefinitions())
                                Console.WriteLine($"  {tool.Name}: {tool.Description}");
                            continue;
                        case "":
                            continue;
                    }

                    var result = await reflection.RunAgentWithReflectionAsync(new AgentRunOptions
                    {
                        Prompt = input,
                        Session = sessionConfig,
                        History = history,
                        Streaming = true,
                        OnText = text => Console.Write(text),
                        OnPermissionRequest = auto ? null : async tc =>
                        {
                            Console.Write($"\nAllow tool '{tc.Name}'? (y/n): ");
                            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                            return answer is "y" or "yes";
                        }
                    }, ct);

                    history = [..result.Messages];
                    Console.WriteLine();

                    await sm.LogUserMessageAsync(sessionConfig, input);
                    await sm.LogAssistantMessageAsync(sessionConfig, result.Text);
                }
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine($"Session {id} not found.");
            }
        });

        // --- solvra session delete <id> ---
        var sessionDeleteArg = new Argument<string>("id", "Session ID to delete");
        var sessionDeleteCmd = new Command("delete", "Delete a session") { sessionDeleteArg };
        sessionDeleteCmd.SetHandler(async (string id) =>
        {
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
            }
        }, sessionDeleteArg);

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

        return await rootCommand.InvokeAsync(args);
    }

    private static (ModelRouter Router, ToolRegistry Registry, HookEngine HookEngine, AuditLogger AuditLogger, SkillLoader SkillLoader, MemoryManager MemoryManager, PermissionChecker PermissionChecker, Tracer Tracer) BuildSubsystems(SolvraConfig config)
    {
        var router = new ModelRouter();
        var auditLogger = new AuditLogger("logs");
        var sandbox = new SandboxManager(new SandboxConfig());
        var registry = new ToolRegistry(auditLogger);
        registry.RegisterBuiltins(sandbox);

        var hookEngine = new HookEngine();
        var skillLoader = new SkillLoader(config.SkillsDir);
        var memoryManager = new MemoryManager(config.MemoryDir);
        var permissionChecker = new PermissionChecker();
        var tracer = new Tracer("Solvra");

        // P5: Subscribe Printer once at process startup (not per AgentLoop instance)
        tracer.OnSpanEvent += Printer.HandleSpanEvent;

        return (router, registry, hookEngine, auditLogger, skillLoader, memoryManager, permissionChecker, tracer);
    }
}
