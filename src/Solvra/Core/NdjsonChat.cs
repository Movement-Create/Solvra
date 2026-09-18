using System.Text.Json;
using System.Threading.Channels;
using Solvra.Models;
using Solvra.Providers;

namespace Solvra.Core;

/// <summary>
/// Machine-readable chat protocol for UI integrations (e.g. the acess app):
/// line-delimited JSON commands on stdin, line-delimited JSON events on stdout.
/// Supports streaming text, tool events, interactive permission prompts,
/// model/mode switching and turn interruption.
/// </summary>
public static class NdjsonChat
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public sealed record ProtocolCommand(string? T, string? Text, string? Id, string? Decision, string? Model, string? Mode);

    /// <summary>Parse and validate one protocol command line. Returns null when the line is not usable.</summary>
    public static ProtocolCommand? ParseCommand(string? line, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(line)) return null;
        ProtocolCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<ProtocolCommand>(line, Json); }
        catch { error = "unparseable command"; return null; }
        if (cmd?.T is null) { error = "missing command type"; return null; }
        return cmd;
    }

    public static async Task RunAsync(
        Reflection reflection,
        SessionManager sessionMgr,
        SessionConfig sessionConfig,
        List<Message> history,
        bool auto,
        bool resumed,
        CancellationToken outerCt)
    {
        var state = new ChatState(reflection, sessionMgr, sessionConfig, history, auto, resumed);
        await state.RunAsync(outerCt);
    }

    private sealed class ChatState
    {
        private readonly Reflection _reflection;
        private readonly SessionManager _sessionMgr;
        private readonly object _outLock = new();
        private readonly object _permLock = new();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _permissions = new();
        private readonly Channel<string> _sends = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        private SessionConfig _sessionConfig;
        private List<Message> _history;
        private bool _auto;
        private volatile bool _closed;
        private volatile string? _pendingModel;
        private volatile string? _pendingMode;
        private CancellationTokenSource _turnCts = new();

        internal ChatState(Reflection reflection, SessionManager sessionMgr, SessionConfig sessionConfig, List<Message> history, bool auto, bool resumed)
        {
            _reflection = reflection;
            _sessionMgr = sessionMgr;
            // The UI owns the permission mode; a resumed session may carry an older one.
            _sessionConfig = sessionConfig with { PermissionMode = auto ? "auto" : "default" };
            _history = history;
            _auto = auto;
            Resumed = resumed;
        }

        private bool Resumed { get; }

        private void Emit(object ev)
        {
            lock (_outLock)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(ev, Json));
                Console.Out.Flush();
            }
        }

        private async Task HandleCommand(string line)
        {
            var cmd = ParseCommand(line, out var error);
            if (error != null) { Emit(new { t = "error", message = error }); return; }
            if (cmd?.T is null) return;
            switch (cmd.T.ToLowerInvariant())
            {
                case "send":
                    if (string.IsNullOrEmpty(cmd.Text)) { Emit(new { t = "error", message = "empty message" }); break; }
                    await _sends.Writer.WriteAsync(cmd.Text);
                    break;
                case "permission":
                    TaskCompletionSource<bool>? tcs = null;
                    lock (_permLock) { if (cmd.Id != null) _permissions.Remove(cmd.Id, out tcs); }
                    tcs?.TrySetResult(cmd.Decision == "allow");
                    break;
                case "model":
                    if (string.IsNullOrEmpty(cmd.Model)) { Emit(new { t = "error", message = "missing model" }); break; }
                    _pendingModel = cmd.Model;
                    Emit(new { t = "model", model = cmd.Model });
                    break;
                case "mode":
                    if (string.IsNullOrEmpty(cmd.Mode)) { Emit(new { t = "error", message = "missing mode" }); break; }
                    _pendingMode = cmd.Mode.ToLowerInvariant() == "auto" ? "auto" : "ask";
                    Emit(new { t = "mode", mode = _pendingMode });
                    break;
                case "interrupt":
                    _turnCts.Cancel();
                    break;
                case "close":
                    _closed = true;
                    _sends.Writer.TryComplete();
                    break;
                default:
                    Emit(new { t = "error", message = $"unknown command {cmd.T}" });
                    break;
            }
        }

        private async Task ReadStdin(CancellationToken ct)
        {
            using var reader = new StreamReader(Console.OpenStandardInput());
            while (!_closed && !ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                if (line == null) break;
                try { await HandleCommand(line); }
                catch (Exception ex) { Emit(new { t = "error", message = ex.Message }); }
            }
            _closed = true;
            _sends.Writer.TryComplete();
        }

        internal async Task RunAsync(CancellationToken outerCt)
        {
            Emit(new { t = "ready", session = _sessionConfig.Id, file = _sessionConfig.FilePath is { } fp ? Path.GetFullPath(fp) : null, model = _sessionConfig.Model, provider = _sessionConfig.Provider, mode = _auto ? "auto" : "ask", resumed = Resumed });

            var stdin = ReadStdin(outerCt);
            try
            {
                while (!_closed && !outerCt.IsCancellationRequested)
                {
                    string prompt;
                    try { prompt = await _sends.Reader.ReadAsync(outerCt); }
                    catch (ChannelClosedException) { break; }
                    catch (OperationCanceledException) { break; }

                    ApplyPendingChanges();
                    await RunTurnAsync(prompt, outerCt);
                }
            }
            catch (OperationCanceledException) { /* process shutdown */ }
            catch (Exception ex)
            {
                Emit(new { t = "error", message = ex.Message, fatal = true });
            }
            finally
            {
                _closed = true;
                _sends.Writer.TryComplete();
                CancelPendingPermissions();
                Emit(new { t = "exit" });
                try { await stdin; } catch { /* reader ends with the stream */ }
            }
        }

        /// <summary>
        /// One user turn. A provider error or an interrupt ends only this turn: the process
        /// keeps reading commands so the UI can retry or continue in the same session.
        /// </summary>
        private async Task RunTurnAsync(string prompt, CancellationToken outerCt)
        {
            _turnCts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, _turnCts.Token);

            // Log the prompt before the loop runs: the agent loop appends tool events as it
            // goes, and resume rebuilds history in file order.
            await _sessionMgr.LogUserMessageAsync(_sessionConfig, prompt);
            Emit(new { t = "start" });
            var streamed = new System.Text.StringBuilder();
            try
            {
                var result = await _reflection.RunAgentWithReflectionAsync(new AgentRunOptions
                {
                    Prompt = prompt,
                    Session = _sessionConfig,
                    History = _history,
                    Streaming = true,
                    OnText = text => { streamed.Append(text); Emit(new { t = "text", delta = text }); },
                    OnPermissionRequest = _auto ? null : RequestPermission,
                    OnToolCall = tc => Emit(new { t = "tool_start", id = tc.Id, name = tc.Name, input = tc.Input }),
                    OnToolResult = tr => Emit(new { t = "tool_end", id = tr.ToolUseId, status = tr.IsError ? "error" : "done", output = tr.Content }),
                }, linked.Token);

                _history = [.. result.Messages];
                await _sessionMgr.LogAssistantMessageAsync(_sessionConfig, result.Text);
                Emit(new
                {
                    t = "turn_end",
                    turns = result.Turns,
                    costUsd = result.CostUsd,
                    input = result.Usage.InputTokens,
                    output = result.Usage.OutputTokens,
                    text = result.Text,
                    stopReason = result.StopReason.ToString().ToLowerInvariant(),
                });
            }
            catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
            {
                CancelPendingPermissions();
                await EndFailedTurnAsync(prompt, streamed.ToString(), "(interrupted)");
                Emit(new { t = "turn_end", interrupted = true, text = streamed.ToString(), stopReason = "interrupted" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CancelPendingPermissions();
                await EndFailedTurnAsync(prompt, streamed.ToString(), $"(turn failed: {ex.Message})");
                Emit(new { t = "error", message = ex.Message });
                Emit(new { t = "turn_end", isError = true, text = streamed.ToString(), stopReason = "error", message = ex.Message });
            }
        }

        /// <summary>Keep user/assistant alternation in memory and on disk after a turn that did not complete.</summary>
        private async Task EndFailedTurnAsync(string prompt, string partial, string note)
        {
            var reply = string.IsNullOrWhiteSpace(partial) ? note : $"{partial}\n\n{note}";
            _history.Add(Message.FromText(MessageRole.User, prompt));
            _history.Add(Message.FromText(MessageRole.Assistant, reply));
            try { await _sessionMgr.LogAssistantMessageAsync(_sessionConfig, reply); }
            catch { /* logging must not take the session down */ }
        }

        private void CancelPendingPermissions()
        {
            lock (_permLock)
            {
                foreach (var tcs in _permissions.Values) tcs.TrySetResult(false);
                _permissions.Clear();
            }
        }

        private void ApplyPendingChanges()
        {
            var model = _pendingModel;
            if (model != null)
            {
                _pendingModel = null;
                // "provider:model" pins the provider (gateway models such as kimi-* or qwen* would
                // otherwise be routed by name to Moonshot or Ollama).
                var colon = model.IndexOf(':');
                var provider = colon > 0 ? model[..colon] : ModelRouter.DetectProvider(model) ?? _sessionConfig.Provider;
                _sessionConfig = _sessionConfig with { Model = model, Provider = provider };
            }
            var mode = _pendingMode;
            if (mode != null)
            {
                _pendingMode = null;
                _auto = mode == "auto";
                _sessionConfig = _sessionConfig with { PermissionMode = _auto ? "auto" : "default" };
            }
        }

        private Task<bool> RequestPermission(ToolCall tc)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_permLock) _permissions[tc.Id] = tcs;
            Emit(new { t = "permission", id = tc.Id, name = tc.Name, input = tc.Input });
            return tcs.Task;
        }
    }
}
