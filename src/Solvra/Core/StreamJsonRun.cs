#nullable enable

using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Solvra.Models;
using Solvra.Security;
using Solvra.Tools;

namespace Solvra.Core;

/// <summary>
/// <c>solvra run --output-format stream-json</c>: one JSON event per line on stdout, control
/// messages as JSON lines on stdin. Events: session, text_delta, thinking_delta, tool_call,
/// permission_request, permission_rule, tool_result, agent_start, agent_end, plan, usage,
/// done, error. Controls: permission_response (allow | allow_always | deny, optional message)
/// and cancel. Every event carries the id of the agent it belongs to ("main" or a subagent id
/// such as "a1", with its parent in agent_start), so a UI can nest parallel subagents.
/// </summary>
public sealed class StreamJsonRun : IAgentObserver
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TextWriter _out;
    private readonly IToolRegistry _registry;
    private readonly string _cwd;
    private readonly string _mainModel;
    private readonly string? _mainProvider;
    private readonly object _outLock = new();
    private readonly ConcurrentDictionary<string, PendingPermission> _pending = new();
    private readonly ConcurrentDictionary<string, string> _denialNotes = new();
    private readonly ConcurrentDictionary<string, ToolPreview> _previews = new();
    private readonly ConcurrentDictionary<string, (TokenUsage Usage, decimal Cost)> _usage = new();
    private readonly List<PermissionRule> _rules = new();
    private readonly object _rulesLock = new();
    private int _nextPermission;
    private int _nextAgent;
    private int _mainContextTokens;
    private volatile bool _controlClosed;

    private sealed record PendingPermission(TaskCompletionSource<PermissionAnswer> Answer, string ToolCallId, string Kind, string? Prefix);
    public sealed record PermissionAnswer(string Decision, string? Message);
    private sealed record ToolPreview(string Name, string Summary, string? Diff, int Added, int Removed, string? Command);

    /// <summary>A remembered "Yes, don't ask again": a whole kind (edit, agent, network, read) or an execute command prefix.</summary>
    public sealed record PermissionRule(string Kind, string? Prefix)
    {
        public override string ToString() => Prefix is null ? Kind : $"{Kind}:{Prefix}";

        /// <summary>Parse "edit", "agent", "network", "read" or "execute:&lt;prefix&gt;".</summary>
        public static PermissionRule? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Trim().Split(':', 2);
            var kind = parts[0].Trim().ToLowerInvariant();
            if (kind is not ("edit" or "execute" or "agent" or "network" or "read")) return null;
            var prefix = parts.Length > 1 ? parts[1].Trim() : null;
            if (kind == "execute") return string.IsNullOrEmpty(prefix) || prefix.Any(char.IsWhiteSpace) ? null : new PermissionRule(kind, prefix);
            return prefix is null ? new PermissionRule(kind, null) : null;
        }
    }

    public StreamJsonRun(TextWriter output, IToolRegistry registry, string cwd, string mainModel, string? mainProvider,
        IEnumerable<PermissionRule>? rules = null)
    {
        _out = output;
        _registry = registry;
        _cwd = cwd;
        _mainModel = mainModel;
        _mainProvider = mainProvider;
        if (rules != null) _rules.AddRange(rules);
    }

    public bool ParallelSubagents => true;

    public IReadOnlyList<PermissionRule> Rules { get { lock (_rulesLock) return _rules.ToList(); } }

    public void Emit(object ev)
    {
        var line = JsonSerializer.Serialize(ev, Json);
        lock (_outLock)
        {
            _out.WriteLine(line);
            _out.Flush();
        }
    }

    // ── IAgentObserver ────────────────────────────────────────────────────────────────

    public AgentHooks HooksFor(string agentId) => new(
        OnText: text => Emit(new { type = "text_delta", agent = agentId, text }),
        OnThinking: text => Emit(new { type = "thinking_delta", agent = agentId, text }),
        OnToolCall: tc => OnToolCall(agentId, tc),
        OnToolResult: tr => OnToolResult(agentId, tr),
        OnPermissionRequest: tc => RequestPermissionAsync(agentId, tc),
        OnUsage: (usage, cost, sentTokens) => OnUsage(agentId, usage, cost, sentTokens));

    public string SubagentStarted(string parentAgentId, string? toolCallId, string title)
    {
        var id = $"a{Interlocked.Increment(ref _nextAgent)}";
        Emit(new { type = "agent_start", agent = id, parent = parentAgentId, tool_call_id = toolCallId, title });
        return id;
    }

    public void SubagentEnded(string agentId, string status) =>
        Emit(new { type = "agent_end", agent = agentId, status });

    public string? TakeDenialNote(string toolCallId) =>
        _denialNotes.TryRemove(toolCallId, out var note) ? note : null;

    // ── Tool events ───────────────────────────────────────────────────────────────────

    private void OnToolCall(string agentId, ToolCall tc)
    {
        var preview = Preview(tc);
        _previews[tc.Id] = preview;
        Emit(new
        {
            type = "tool_call", agent = agentId, id = tc.Id, name = tc.Name, input = tc.Input, summary = preview.Summary,
            kind = KindOf(_registry.GetTool(tc.Name)),
        });
    }

    private void OnToolResult(string agentId, ToolResult tr)
    {
        _previews.TryRemove(tr.ToolUseId, out var preview);
        var output = tr.Content ?? "";
        object meta = preview is { Diff: not null } && !tr.IsError
            ? new { added = preview.Added, removed = preview.Removed, lines = CountLines(output) }
            : new { added = (int?)null, removed = (int?)null, lines = CountLines(output) };
        Emit(new
        {
            type = "tool_result", agent = agentId, id = tr.ToolUseId, ok = !tr.IsError,
            output = output.Length > 20_000 ? output[..20_000] + "\n…" : output,
            diff = tr.IsError ? null : preview?.Diff,
            meta,
        });
        if (preview?.Name == "todo" && !tr.IsError) EmitPlan(agentId);
    }

    private void EmitPlan(string agentId)
    {
        // The todo list is per session; subagents keep their own. Only the main run's list is the plan.
        if (agentId != "main" || CurrentSessionId is null) return;
        var items = TodoTool.Snapshot(CurrentSessionId)
            .Select(i => new { text = i.Task, status = i.Status switch { "done" => "done", "in_progress" => "active", _ => "pending" } })
            .ToList();
        Emit(new { type = "plan", agent = agentId, items });
    }

    /// <summary>Set by the caller once the session id is known, so plan events can read the todo list.</summary>
    public string? CurrentSessionId { get; set; }

    private void OnUsage(string agentId, TokenUsage usage, decimal cost, int sentTokens)
    {
        _usage[agentId] = (usage, cost);
        if (agentId == "main") _mainContextTokens = sentTokens;
        var (input, output, total) = Totals();
        var limit = Context.GetContextLimit(_mainModel, _mainProvider);
        var pct = limit > 0 ? (int)Math.Clamp(Math.Round(100.0 * _mainContextTokens / limit), 0, 100) : 0;
        Emit(new { type = "usage", agent = agentId, input, output, cost_usd = total, context_pct = pct });
    }

    /// <summary>Tokens and cost across the main run and every subagent.</summary>
    public (int Input, int Output, decimal CostUsd) Totals()
    {
        int input = 0, output = 0; decimal cost = 0;
        foreach (var (u, c) in _usage.Values) { input += u.InputTokens; output += u.OutputTokens; cost += c; }
        return (input, output, cost);
    }

    // ── Permissions ───────────────────────────────────────────────────────────────────

    public static string KindOf(ITool? tool) => tool?.PermissionLevel switch
    {
        PermissionLevel.Write => "edit",
        PermissionLevel.Execute => "execute",
        PermissionLevel.Agent => "agent",
        PermissionLevel.Network => "network",
        _ => "read",
    };

    /// <summary>
    /// The command prefix an "allow always" answer remembers: the first word of a simple command.
    /// Commands with shell operators (;, &amp;, |, redirects, substitutions) have none, so they always ask.
    /// </summary>
    public static string? CommandPrefix(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        if (c.IndexOfAny([';', '&', '|', '>', '<', '`', '$', '(', ')', '\n', '\r']) >= 0) return null;
        var first = c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        return first.Contains('=') ? null : first; // FOO=bar cmd: environment prefixes always ask
    }

    public bool IsAllowedByRule(string kind, string? command)
    {
        lock (_rulesLock)
        {
            foreach (var r in _rules)
            {
                if (r.Kind != kind) continue;
                if (kind != "execute") return true;
                if (r.Prefix != null && CommandPrefix(command) == r.Prefix) return true;
            }
        }
        return false;
    }

    public async Task<bool> RequestPermissionAsync(string agentId, ToolCall tc)
    {
        var preview = _previews.TryGetValue(tc.Id, out var p) ? p : Preview(tc);
        var kind = KindOf(_registry.GetTool(tc.Name));
        if (IsAllowedByRule(kind, preview.Command)) return true;

        if (_controlClosed) return false; // stdin is gone: nobody can answer, so fail closed

        var id = $"p{Interlocked.Increment(ref _nextPermission)}";
        var prefix = kind == "execute" ? CommandPrefix(preview.Command) : null;
        var pending = new PendingPermission(new TaskCompletionSource<PermissionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously), tc.Id, kind, prefix);
        _pending[id] = pending;
        if (_controlClosed) { _pending.TryRemove(id, out _); return false; }
        Emit(new
        {
            type = "permission_request", id, agent = agentId, tool_call_id = tc.Id, tool = tc.Name, kind,
            summary = preview.Summary, diff = preview.Diff, command = preview.Command,
            // What "Yes, don't ask again" would remember; null when it is not offered.
            always = kind == "execute" ? (prefix is null ? null : $"execute:{prefix}") : kind,
        });

        var answer = await pending.Answer.Task;
        _pending.TryRemove(id, out _);
        switch (answer.Decision)
        {
            case "allow":
                return true;
            case "allow_always":
                var rule = kind == "execute" ? (prefix is null ? null : new PermissionRule(kind, prefix)) : new PermissionRule(kind, null);
                if (rule != null)
                {
                    lock (_rulesLock) if (!_rules.Contains(rule)) _rules.Add(rule);
                    Emit(new { type = "permission_rule", rule = rule.ToString(), kind = rule.Kind, prefix = rule.Prefix });
                }
                return true;
            default:
                if (!string.IsNullOrWhiteSpace(answer.Message)) _denialNotes[tc.Id] = answer.Message.Trim();
                return false;
        }
    }

    /// <summary>Deny every open request (cancel, or stdin closed).</summary>
    public void DenyAllPending()
    {
        foreach (var (id, pending) in _pending)
            if (_pending.TryRemove(id, out _)) pending.Answer.TrySetResult(new PermissionAnswer("deny", null));
    }

    // ── Control channel ───────────────────────────────────────────────────────────────

    public sealed record ControlMessage(string? Type, string? Id, string? Decision, string? Message);

    /// <summary>Apply one control line. Returns false for lines that are not valid controls (an error event is emitted).</summary>
    public bool HandleControl(string? line, CancellationTokenSource runCts)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;
        ControlMessage? msg;
        try { msg = JsonSerializer.Deserialize<ControlMessage>(line, Json); }
        catch (JsonException) { EmitError("Could not parse a control message on stdin.", null); return false; }
        switch (msg?.Type)
        {
            case "permission_response":
                var decision = msg.Decision?.ToLowerInvariant();
                if (msg.Id is null || decision is not ("allow" or "allow_always" or "deny"))
                {
                    EmitError("permission_response needs an id and a decision of allow, allow_always or deny.", null);
                    return false;
                }
                if (_pending.TryGetValue(msg.Id, out var pending))
                    pending.Answer.TrySetResult(new PermissionAnswer(decision, msg.Message));
                return true;
            case "cancel":
                DenyAllPending();
                try { runCts.Cancel(); } catch (ObjectDisposedException) { }
                return true;
            default:
                EmitError($"Unknown control message type '{msg?.Type}'.", null);
                return false;
        }
    }

    /// <summary>Read control lines until EOF or cancellation. EOF denies open requests: nobody is left to answer.</summary>
    public async Task ReadControlAsync(TextReader input, CancellationTokenSource runCts, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(ct);
                if (line == null) break;
                HandleControl(line, runCts);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        _controlClosed = true;
        DenyAllPending();
    }

    public void EmitError(string message, string? fix, string? provider = null) =>
        Emit(new { type = "error", message, fix, provider });

    /// <summary>Map an error message to the button the UI should offer.</summary>
    public static string? FixFor(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = message.ToLowerInvariant();
        if (m.Contains("api key") || m.Contains("api_key") || m.Contains("401") || m.Contains("unauthorized") || m.Contains("403") || m.Contains("authentication") || m.Contains("not logged in") || m.Contains("login"))
            return "add_api_key";
        if (m.Contains("429") || m.Contains("rate limit") || m.Contains("quota") || m.Contains("usage limit"))
            return "rate_limited";
        if (m.Contains("timed out") || m.Contains("timeout") || m.Contains("connection") || m.Contains("network") || m.Contains("name resolution") || m.Contains("no such host"))
            return "retry";
        return null;
    }

    // ── Previews ──────────────────────────────────────────────────────────────────────

    private string Rel(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            var full = Path.GetFullPath(path, _cwd);
            var rel = Path.GetRelativePath(_cwd, full);
            return rel.StartsWith("..", StringComparison.Ordinal) ? full : rel;
        }
        catch { return path; }
    }

    private ToolPreview Preview(ToolCall tc)
    {
        string? Get(string key) => tc.Input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var path = Get("path");
        switch (tc.Name)
        {
            case "file_edit":
            {
                var diff = EditDiff(path, Get("old_string"), Get("new_string"),
                    tc.Input.TryGetValue("replace_all", out var ra) && ra.ValueKind == JsonValueKind.True);
                return new ToolPreview(tc.Name, $"Edit {Rel(path)}", diff?.Text, diff?.Added ?? 0, diff?.Removed ?? 0, null);
            }
            case "file_write":
            {
                UnifiedDiff.Result? diff = null;
                string verb = "Write";
                try
                {
                    var full = Path.GetFullPath(path ?? "", _cwd);
                    var old = File.Exists(full) ? File.ReadAllText(full) : null;
                    if (old is null) verb = "Create";
                    diff = UnifiedDiff.Create(old, Get("content") ?? "", Rel(path));
                }
                catch { }
                return new ToolPreview(tc.Name, $"{verb} {Rel(path)}", diff?.Text, diff?.Added ?? 0, diff?.Removed ?? 0, null);
            }
            case "bash":
            {
                var command = Get("command");
                return new ToolPreview(tc.Name, $"Run {Short(command)}", null, 0, 0, command);
            }
            case "code_run":
                return new ToolPreview(tc.Name, $"Run {Get("language") ?? "code"} snippet", null, 0, 0, Get("code"));
            case "file_read": return Simple(tc, $"Read {Rel(path)}");
            case "glob": return Simple(tc, $"Find {Get("pattern")}");
            case "grep": return Simple(tc, $"Search {Get("pattern")}");
            case "web_fetch": return Simple(tc, $"Fetch {Get("url")}");
            case "web_search": return Simple(tc, $"Search the web for {Short(Get("query"))}");
            case "agent": return Simple(tc, $"Agent: {AgentTool.SubagentTitle(Get("description"), Get("prompt") ?? "")}");
            case "todo": return Simple(tc, "Update plan");
            default:
                var summary = Observability.Printer.SummarizeInput(tc.Name, JsonSerializer.SerializeToElement(tc.Input));
                return Simple(tc, $"{tc.Name} {summary}".Trim());
        }
    }

    private static ToolPreview Simple(ToolCall tc, string summary) => new(tc.Name, summary, null, 0, 0, null);

    private static string Short(string? s)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length > 80 ? s[..79] + "…" : s;
    }

    private UnifiedDiff.Result? EditDiff(string? path, string? oldString, string? newString, bool replaceAll)
    {
        if (path is null || newString is null) return null;
        try
        {
            var full = Path.GetFullPath(path, _cwd);
            if (string.IsNullOrEmpty(oldString))
                return UnifiedDiff.Create(File.Exists(full) ? File.ReadAllText(full) : null, newString, Rel(path));
            if (!File.Exists(full)) return null;
            var old = File.ReadAllText(full);
            var at = old.IndexOf(oldString, StringComparison.Ordinal);
            if (at < 0) return null;
            var updated = replaceAll ? old.Replace(oldString, newString, StringComparison.Ordinal)
                : old[..at] + newString + old[(at + oldString.Length)..];
            return UnifiedDiff.Create(old, updated, Rel(path));
        }
        catch { return null; }
    }

    private static int CountLines(string s) => s.Length == 0 ? 0 : s.Count(c => c == '\n') + (s.EndsWith('\n') ? 0 : 1);
}
