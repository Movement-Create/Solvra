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
        var router = new ModelRouter(config.ModelTimeoutSeconds);
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
    private static void ConfigureSubagents(AgentSubsystems s) =>
        AgentTool.RunAgentDelegate = SubagentRunner(s.Config, s.Router, s.Registry, s.HookEngine, s.AuditLogger, s.Tracer);

    /// <summary>The subagent runner used by the agent tool (also constructed directly by tests).</summary>
    internal static Func<SubagentRequest, CancellationToken, Task<string>> SubagentRunner(
        SolvraConfig config, ModelRouter router, ToolRegistry registry, HookEngine hookEngine, AuditLogger auditLogger, Tracer tracer)
    {
        return async (req, ct) =>
        {
            var parent = req.Parent;
            var model = req.Model ?? parent.Model ?? config.Model;
            var provider = ModelRouter.ChooseProvider(model, explicitProvider: null,
                configuredProvider: parent.Provider ?? config.Provider, configuredIsExplicit: true);

            var loop = new AgentLoop(router, registry, hookEngine, auditLogger, tracer: tracer);
            // With a live observer (stream-json) the child streams its own text, tools, approvals
            // and usage under its own agent id, nested under the parent.
            var observer = parent.Observer;
            var agentId = observer?.SubagentStarted(parent.AgentId, parent.OperationId, req.Title ?? AgentTool.SubagentTitle(null, req.Prompt));
            var hooks = agentId is null ? null : observer!.HooksFor(agentId);
            AgentRunResult result;
            try
            {
                result = await loop.RunAsync(new AgentRunOptions
                {
                    Prompt = req.Prompt,
                    Session = new SessionConfig
                    {
                        Id = Guid.NewGuid().ToString(),
                        CreatedAt = DateTime.UtcNow.ToString("o"),
                        Model = model,
                        Provider = provider,
                        Effort = req.Effort,
                        MaxTurns = req.MaxTurns,
                        MaxBudgetUsd = config.MaxBudgetUsd,
                        MaxTokens = config.MaxTokens,
                        PermissionMode = parent.Session.PermissionModeStr,
                        AllowedTools = parent.Session.AllowedTools,
                        DisallowedTools = parent.Session.DisallowedTools,
                    },
                    SystemPrompt = req.SystemPrompt,
                    Streaming = hooks is not null,
                    OnText = hooks?.OnText,
                    OnThinking = hooks?.OnThinking,
                    OnToolCall = hooks?.OnToolCall,
                    OnToolResult = hooks?.OnToolResult,
                    OnUsage = hooks?.OnUsage,
                    Observer = observer,
                    AgentId = agentId ?? "main",
                    SubagentDepth = req.Depth,
                    SubagentsEnabled = config.SubagentsEnabled,
                    SubagentModel = config.SubagentModel,
                    SubagentEffort = config.ParsedSubagentEffort,
                    Deadline = parent.Deadline,
                    ProcessTracker = parent.ProcessTracker,
                    // No parent approver means auto or fail-closed; keep that rather than asking through the stream.
                    OnPermissionRequest = hooks is null || parent.PermissionRequest is null ? parent.PermissionRequest : hooks.OnPermissionRequest,
                    Cwd = parent.Cwd,
                    LogToSession = false,
                }, ct);
            }
            catch (OperationCanceledException)
            {
                if (agentId is not null) observer!.SubagentEnded(agentId, "cancelled");
                throw;
            }
            catch
            {
                if (agentId is not null) observer!.SubagentEnded(agentId, "error");
                throw;
            }
            if (agentId is not null)
                observer!.SubagentEnded(agentId, result.StopReason == StopReason.Error ? "error" : "ok");

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

    /// <summary>Values accepted by <c>--permission-mode</c>, mapped to the session's stored mode.</summary>
    public static string? ParsePermissionMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "default" or "ask" => "default",
        "askall" or "ask-all" => "askall",
        "plan" => "plan",
        "auto" => "auto",
        _ => null,
    };

    /// <summary>
    /// Effective permission mode for a run. <c>--plan</c> always wins (read-only is the safest
    /// answer to a conflict); an explicit <c>--permission-mode</c> then beats <c>--auto</c>,
    /// <c>--ask-all</c> and the config default, so a caller that pins "default" can never be
    /// turned into auto by a <c>permission_mode</c> in the user's config.
    /// </summary>
    public static string ResolvePermissionMode(string? explicitMode, bool plan, bool auto, bool askAll, string configMode)
    {
        if (plan) return "plan";
        if (ParsePermissionMode(explicitMode) is { } pinned) return pinned;
        if (auto) return "auto";
        if (askAll) return "askall";
        return configMode;
    }

    /// <summary>Stderr marker for a tool refused because nobody could approve it. Parsed by the VS Code extension.</summary>
    public const string ApprovalNeededMarker = "[solvra:approval-needed]";

    /// <summary>
    /// Tool hooks that write <see cref="ApprovalNeededMarker"/> lines to <paramref name="err"/> whenever a
    /// tool is refused for lack of an approver, so a non-interactive caller can tell the user why.
    /// </summary>
    public static (Action<ToolCall> OnCall, Action<ToolResult> OnResult) ApprovalNeededReporter(TextWriter err)
    {
        var names = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        return (
            tc => names[tc.Id] = tc.Name,
            tr =>
            {
                if (!tr.IsError || !tr.Content.Contains(ToolRegistry.NoApproverPhrase, StringComparison.Ordinal)) return;
                var name = names.TryRemove(tr.ToolUseId, out var n) ? n : "tool";
                lock (err) err.WriteLine($"{ApprovalNeededMarker} {name}");
            });
    }

    /// <summary>Exit code: 0 done, 1 error, 2 turn limit, 3 budget limit, 4 deadline.</summary>
    public static int ExitCode(StopReason reason) => reason switch
    {
        StopReason.Text => 0,
        StopReason.Error => 1,
        StopReason.MaxTurns => 2,
        StopReason.MaxBudget => 3,
        StopReason.Deadline => 4,
        _ => 1
    };
}
