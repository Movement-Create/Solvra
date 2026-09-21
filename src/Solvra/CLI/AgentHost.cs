#nullable enable

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

namespace Solvra.CLI;

/// <summary>Everything an agent run needs, built once per process.</summary>
public sealed record AgentSubsystems(
    SolvraConfig Config,
    ModelRouter Router,
    ToolRegistry Registry,
    HookEngine HookEngine,
    AuditLogger AuditLogger,
    SkillLoader SkillLoader,
    MemoryManager MemoryManager,
    PermissionChecker PermissionChecker,
    Tracer Tracer,
    SandboxManager Sandbox)
{
    public AgentLoop CreateLoop() =>
        new(Router, Registry, HookEngine, AuditLogger, SkillLoader, MemoryManager, PermissionChecker, tracer: Tracer);

    public Reflection CreateReflection() => new(CreateLoop(), Config.Reflection);
}

public static class AgentHost
{
    public static AgentSubsystems Build(SolvraConfig config)
    {
        var router = new ModelRouter();
        var auditLogger = new AuditLogger(SolvraPaths.LogsDir);
        var sandbox = new SandboxManager(new SandboxConfig());
        var registry = new ToolRegistry(auditLogger);
        registry.RegisterBuiltins(sandbox);

        var hookEngine = new HookEngine();
        ShellCommandHook.RegisterFromConfig(hookEngine, config.Hooks);

        var skillLoader = new SkillLoader(config.SkillsDir);
        var memoryManager = new MemoryManager(config.MemoryDir);
        var permissionChecker = new PermissionChecker();
        var tracer = new Tracer(Path.Combine(SolvraPaths.LogsDir, "traces.jsonl"));

        if (Enum.TryParse<ObservabilityLevel>(config.Observability.Level, true, out var level))
            Printer.SetLevel(config.Observability.Enabled ? level : ObservabilityLevel.Off);
        tracer.OnSpanEvent += Printer.HandleSpanEvent;

        var subsystems = new AgentSubsystems(config, router, registry, hookEngine, auditLogger, skillLoader,
            memoryManager, permissionChecker, tracer, sandbox);
        ConfigureSubagents(subsystems);
        return subsystems;
    }

    /// <summary>
    /// Subagents inherit the parent's permission mode and approval prompt (so plan mode and
    /// "ask" mode still apply), its working directory, provider, allow/deny lists and budget,
    /// and carry their nesting depth so <see cref="AgentTool.MaxSubagentDepth"/> is enforced.
    /// </summary>
    private static void ConfigureSubagents(AgentSubsystems s)
    {
        AgentTool.RunAgentDelegate = async (req, ct) =>
        {
            var parent = req.Parent;
            var model = req.Model ?? parent.Model ?? s.Config.Model;
            var provider = ModelRouter.ChooseProvider(model, explicitProvider: null,
                configuredProvider: parent.Provider ?? s.Config.Provider, configuredIsExplicit: true);

            var loop = new AgentLoop(s.Router, s.Registry, s.HookEngine, s.AuditLogger, tracer: s.Tracer);
            var result = await loop.RunAsync(new AgentRunOptions
            {
                Prompt = req.Prompt,
                Session = new SessionConfig
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    Model = model,
                    Provider = provider,
                    MaxTurns = req.MaxTurns,
                    MaxBudgetUsd = s.Config.MaxBudgetUsd,
                    MaxTokens = s.Config.MaxTokens,
                    PermissionMode = parent.Session.PermissionModeStr,
                    AllowedTools = parent.Session.AllowedTools,
                    DisallowedTools = parent.Session.DisallowedTools,
                },
                SystemPrompt = req.SystemPrompt,
                Streaming = false,
                SubagentDepth = req.Depth,
                OnPermissionRequest = parent.PermissionRequest,
                Cwd = parent.Cwd,
                LogToSession = false,
            }, ct);

            return result.StopReason switch
            {
                StopReason.Error => throw new InvalidOperationException(result.Error ?? result.Text),
                StopReason.Text => result.Text,
                _ => $"{result.Text}\n[Subagent stopped: {result.StopReason}]"
            };
        };
    }

    /// <summary>
    /// Decide provider and model for a new session from CLI options and config.
    /// An explicit --provider or "provider:model" wins over name guessing; with --provider
    /// but no --model, a configured model that belongs to another provider is replaced by
    /// that provider's default for the effort level.
    /// </summary>
    public static (string Provider, string Model) ResolveTarget(SolvraConfig config, string? providerOpt, string? modelOpt, EffortLevel effort)
    {
        var model = modelOpt ?? config.Model;
        var provider = ModelRouter.ChooseProvider(model, providerOpt, config.Provider, config.ProviderIsExplicit);

        if (providerOpt != null && modelOpt == null && !string.Equals(providerOpt, config.Provider, StringComparison.OrdinalIgnoreCase)
            && ModelRouter.DetectProvider(config.Model) is { } natural && natural != providerOpt)
        {
            model = ModelRouter.GetEffortModel(providerOpt, effort);
        }
        return (provider, model);
    }

    /// <summary>Interactive y/n prompt used by the plain-text CLIs.</summary>
    public static Task<bool> AskOnConsole(ToolCall tc)
    {
        var summary = Printer.SummarizeInput(tc.Name, System.Text.Json.JsonSerializer.SerializeToElement(tc.Input));
        Console.Error.Write($"\nAllow {tc.Name}{(summary.Length > 0 ? $" ({summary})" : "")}? [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return Task.FromResult(answer is "y" or "yes");
    }

    /// <summary>Exit code for a finished run: 0 done, 1 error, 2 turn limit, 3 budget limit.</summary>
    public static int ExitCode(StopReason reason) => reason switch
    {
        StopReason.Text => 0,
        StopReason.Error => 1,
        StopReason.MaxTurns => 2,
        StopReason.MaxBudget => 3,
        _ => 1
    };
}
