#nullable enable

using System.Text;
using Solvra.Core;
using Solvra.Models;
using Solvra.Providers;

namespace Solvra.CLI;

/// <summary>
/// The plain-text interactive chat used by `solvra chat` and `solvra session resume`.
/// Ctrl+C interrupts the running turn (a second Ctrl+C at the prompt exits); a failed
/// turn never ends the session.
/// </summary>
public sealed class ChatRepl
{
    private readonly AgentSubsystems _s;
    private readonly Reflection _reflection;
    private SessionConfig _session;
    private List<Message> _history;
    private bool _auto;
    private decimal _costUsd;
    private TokenUsage _usage = new();
    private CancellationTokenSource? _turnCts;

    public ChatRepl(AgentSubsystems subsystems, SessionConfig session, List<Message> history, bool auto)
    {
        _s = subsystems;
        _reflection = subsystems.CreateReflection();
        _session = session;
        _history = history;
        _auto = auto || session.PermissionMode == "auto";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"Solvra Chat ({_session.Model}, mode {_session.PermissionMode}) — /help for commands, Ctrl+C interrupts a turn");

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (_turnCts is { IsCancellationRequested: false } turn)
            {
                e.Cancel = true; // keep the process; stop only the turn
                turn.Cancel();
            }
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var input = ReadInput();
                if (input == null) break;
                if (input.Length == 0) continue;

                if (input.StartsWith('/'))
                {
                    if (!HandleCommand(input)) return;
                    continue;
                }

                await RunTurnAsync(input, ct);
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Read one message. A line ending in '\' continues; """ starts/ends a block.</summary>
    private static string? ReadInput()
    {
        Console.Write("\nyou> ");
        var first = Console.ReadLine();
        if (first == null) return null;

        if (first.Trim() == "\"\"\"")
        {
            var block = new StringBuilder();
            while (Console.ReadLine() is { } l && l.Trim() != "\"\"\"")
                block.AppendLine(l);
            return block.ToString().Trim();
        }

        var sb = new StringBuilder();
        var line = first;
        while (line.EndsWith('\\'))
        {
            sb.AppendLine(line[..^1]);
            Console.Write("...> ");
            line = Console.ReadLine() ?? "";
        }
        sb.Append(line);
        return sb.ToString().Trim();
    }

    private bool HandleCommand(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arg = parts.Length > 1 ? parts[1] : null;
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit" or "/quit" or "/q":
                Console.WriteLine("Goodbye!");
                return false;
            case "/help":
                Console.WriteLine("""
                    /model <name>   switch model (use provider:model to pin a provider)
                    /mode ask|auto|plan   permission mode (plan = read-only)
                    /clear          start a fresh conversation in this session
                    /compact        drop older exchanges, keep the last few
                    /cost           tokens and estimated cost so far
                    /session        session id, model, file
                    /tools          list tools
                    /exit           quit
                    Multiline: end a line with \ to continue, or wrap text in lines of three double quotes.
                    """);
                return true;
            case "/session":
                Console.WriteLine($"Session: {_session.Id}\nModel: {_session.Model} ({_session.Provider})\nMode: {_session.PermissionMode}\nFile: {_session.FilePath}");
                Console.WriteLine($"Exchanges: {_history.Count(m => m.Role == MessageRole.User && m.Content.Any(c => c is TextContent))}");
                return true;
            case "/tools":
                foreach (var tool in _s.Registry.GetToolDefinitions())
                    Console.WriteLine($"  {tool.Name}: {tool.Description}");
                return true;
            case "/model":
                if (string.IsNullOrEmpty(arg)) { Console.WriteLine($"Model: {_session.Model} ({_session.Provider})"); return true; }
                var provider = ModelRouter.ChooseProvider(arg, null, _session.Provider, configuredIsExplicit: true);
                _session = _session with { Model = arg, Provider = provider };
                Console.WriteLine($"Model set to {arg} ({provider}).");
                return true;
            case "/mode":
                var mode = arg?.ToLowerInvariant() switch { "auto" => "auto", "plan" => "plan", "ask" or "default" => "default", _ => null };
                if (mode == null) { Console.WriteLine("Usage: /mode ask|auto|plan"); return true; }
                _auto = mode == "auto";
                _session = _session with { PermissionMode = mode };
                Console.WriteLine($"Mode: {mode}");
                return true;
            case "/clear":
                _history = [];
                Console.WriteLine("Conversation cleared.");
                return true;
            case "/compact":
                var starts = _history.Select((m, i) => (m, i))
                    .Where(x => x.m.Role == MessageRole.User && x.m.Content.Any(c => c is TextContent)).Select(x => x.i).ToList();
                if (starts.Count <= 2) { Console.WriteLine("Nothing to compact."); return true; }
                var keepFrom = starts[^2];
                var dropped = keepFrom;
                _history = [Message.FromText(MessageRole.User, "(earlier conversation compacted)"),
                    Message.FromText(MessageRole.Assistant, $"[{dropped} earlier messages were dropped by /compact.]"),
                    .. _history.Skip(keepFrom)];
                Console.WriteLine($"Dropped {dropped} messages.");
                return true;
            case "/cost":
                Console.WriteLine($"Tokens: {_usage.InputTokens} in / {_usage.OutputTokens} out; estimated cost ${_costUsd:F4}");
                return true;
            default:
                Console.WriteLine($"Unknown command {parts[0]} (try /help).");
                return true;
        }
    }

    private async Task RunTurnAsync(string input, CancellationToken ct)
    {
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamed = new StringBuilder();
        try
        {
            var result = await _reflection.RunAgentWithReflectionAsync(new AgentRunOptions
            {
                Prompt = input,
                Session = _session,
                History = _history,
                Streaming = true,
                OnText = text => { streamed.Append(text); Console.Write(text); },
                OnPermissionRequest = _auto ? null : AgentHost.AskOnConsole,
            }, _turnCts.Token);

            _history = [.. result.Messages];
            _usage += result.Usage;
            _costUsd += result.CostUsd;
            Console.WriteLine();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine("\n[interrupted]");
            _history.Add(Message.FromText(MessageRole.User, input));
            _history.Add(Message.FromText(MessageRole.Assistant, streamed.Length > 0 ? streamed + "\n\n(interrupted by the user)" : "(interrupted by the user)"));
            await SafeLog(() => new SessionManager(_s.Config.SessionsDir).LogAssistantMessageAsync(_session, "(interrupted by the user)"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"\n[error] {ex.Message}");
            _history.Add(Message.FromText(MessageRole.User, input));
            _history.Add(Message.FromText(MessageRole.Assistant, $"(turn failed: {ex.Message})"));
        }
        finally
        {
            _turnCts.Dispose();
            _turnCts = null;
        }
    }

    private static async Task SafeLog(Func<Task> log)
    {
        try { await log(); } catch { /* logging must not end the chat */ }
    }
}
