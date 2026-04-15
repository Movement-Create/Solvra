using System.CommandLine;
using System.Text.Json;
using Solvra.Config;
using Solvra.Core;
using Solvra.Hooks;
using Solvra.Memory;
using Solvra.Models;
using Solvra.Observability;
using Solvra.Providers;
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
            Console.WriteLine($"Solvra serve is a placeholder. Port: {port}");
            Console.WriteLine("Webhook and cron server not yet implemented.");
            await Task.Delay(Timeout.Infinite, context.GetCancellationToken());
        });

        // --- solvra memory prune ---
        var memoryCommand = new Command("memory", "Memory management");
        var pruneCommand = new Command("prune", "Prune stale lessons");
        pruneCommand.SetHandler(async () =>
        {
            Console.WriteLine("Memory prune: not yet implemented.");
        });
        memoryCommand.AddCommand(pruneCommand);

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

        sessionCommand.AddCommand(sessionListCommand);
        sessionCommand.AddCommand(sessionShowCommand);

        rootCommand.AddCommand(runCommand);
        rootCommand.AddCommand(chatCommand);
        rootCommand.AddCommand(modelsCommand);
        rootCommand.AddCommand(serveCommand);
        rootCommand.AddCommand(memoryCommand);
        rootCommand.AddCommand(sessionCommand);

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

        return (router, registry, hookEngine, auditLogger, skillLoader, memoryManager, permissionChecker, tracer);
    }
}
