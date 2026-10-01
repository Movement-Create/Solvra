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

    public sealed record ProtocolImage(string? Mime, string? Data);
    public sealed record ProtocolCommand(string? T, string? Text, string? Id, string? Decision, string? Model, string? Mode,
        IReadOnlyList<ProtocolImage>? Images);
    private sealed record SendRequest(string Text, Message UserMessage);

    public const int MaxImages = 4;
    public const int MaxImageBytes = 20 * 1024 * 1024;
    private static readonly HashSet<string> SupportedImageTypes = ["image/png", "image/jpeg", "image/webp"];

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

    public static Message? BuildUserMessage(ProtocolCommand command, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(command.Text)) { error = "empty message"; return null; }
        var images = command.Images ?? [];
        if (images.Count > MaxImages) { error = $"at most {MaxImages} images are allowed"; return null; }

        var content = new List<MessageContent> { new TextContent { Text = command.Text } };
        foreach (var image in images)
        {
            var mime = image.Mime?.ToLowerInvariant();
            if (mime == null || !SupportedImageTypes.Contains(mime))
            {
                error = "supported image types are image/png, image/jpeg and image/webp";
                return null;
            }
            if (string.IsNullOrWhiteSpace(image.Data)) { error = "image data is required"; return null; }
            var maxBase64Length = ((MaxImageBytes + 2L) / 3L) * 4L;
            if (image.Data.Length > maxBase64Length) { error = $"each image must be at most {MaxImageBytes / 1024 / 1024} MiB"; return null; }
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(image.Data);
                if (decoded.Length > MaxImageBytes) { error = $"each image must be at most {MaxImageBytes / 1024 / 1024} MiB"; return null; }
            }
            catch (FormatException) { error = "image data must be valid base64"; return null; }
            if (!MatchesImageType(mime, decoded)) { error = $"image data does not match {mime}"; return null; }
            content.Add(new ImageContent
            {
                Source = new ImageSource { SourceType = "base64", MediaType = mime, Data = image.Data }
            });
        }
        return new Message { Role = MessageRole.User, Content = content, Timestamp = DateTime.UtcNow.ToString("o") };
    }

    private static bool MatchesImageType(string mime, ReadOnlySpan<byte> data) => mime switch
    {
        "image/png" => data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff,
        "image/webp" => data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };

    internal static bool IsToolFreeTurn(Message message, bool noTools) =>
        noTools || message.Content.OfType<ImageContent>().Any();

    public static async Task RunAsync(
        Reflection reflection,
        SessionManager sessionMgr,
        SessionConfig sessionConfig,
        List<Message> history,
        bool auto,
        bool resumed,
        bool noTools,
        bool ephemeral,
        CancellationToken outerCt)
    {
        var state = new ChatState(reflection, sessionMgr, sessionConfig, history, auto, resumed, noTools, ephemeral);
        await state.RunAsync(outerCt);
    }

    private sealed class ChatState
    {
        private readonly Reflection _reflection;
        private readonly SessionManager _sessionMgr;
        private readonly object _outLock = new();
        private readonly object _permLock = new();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _permissions = new();
        private readonly Channel<SendRequest> _sends = Channel.CreateUnbounded<SendRequest>(new UnboundedChannelOptions { SingleReader = true });
        private readonly bool _noTools;
        private readonly bool _ephemeral;

        private SessionConfig _sessionConfig;
        private List<Message> _history;
        private bool _auto;
        private volatile bool _closed;
        private volatile string? _pendingModel;
        private volatile string? _pendingMode;
        private CancellationTokenSource _turnCts = new();

        internal ChatState(Reflection reflection, SessionManager sessionMgr, SessionConfig sessionConfig, List<Message> history,
            bool auto, bool resumed, bool noTools, bool ephemeral)
        {
            _reflection = reflection;
            _sessionMgr = sessionMgr;
            // The UI owns the permission mode; a resumed session may carry an older one.
            _sessionConfig = sessionConfig with
            {
                PermissionMode = auto ? "auto" : sessionConfig.PermissionMode.ToLowerInvariant() switch
                {
                    "plan" => "plan",
                    "askall" or "ask-all" => "askall",
                    _ => "default"
                }
            };
            _history = history;
            _auto = auto;
            _noTools = noTools;
            _ephemeral = ephemeral;
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
            if (error != null) { EmitError("invalid_request", error); return; }
            if (cmd?.T is null) return;
            switch (cmd.T.ToLowerInvariant())
            {
                case "send":
                    var message = BuildUserMessage(cmd, out var validationError);
                    if (message == null) { EmitError("invalid_request", validationError!); break; }
                    await _sends.Writer.WriteAsync(new SendRequest(cmd.Text!, message));
                    break;
                case "permission":
                    TaskCompletionSource<bool>? tcs = null;
                    lock (_permLock) { if (cmd.Id != null) _permissions.Remove(cmd.Id, out tcs); }
                    tcs?.TrySetResult(cmd.Decision == "allow");
                    break;
                case "model":
                    if (string.IsNullOrEmpty(cmd.Model)) { EmitError("invalid_request", "missing model"); break; }
                    _pendingModel = cmd.Model;
                    Emit(new { t = "model", model = cmd.Model });
                    break;
                case "mode":
                    if (string.IsNullOrEmpty(cmd.Mode)) { EmitError("invalid_request", "missing mode"); break; }
                    _pendingMode = cmd.Mode.ToLowerInvariant() switch { "auto" => "auto", "plan" => "plan", "ask-all" => "ask-all", _ => "ask" };
                    Emit(new { t = "mode", mode = _pendingMode });
                    break;
                case "interrupt":
                    _turnCts.Cancel();
                    break;
                case "close":
                    _closed = true;
                    _sends.Writer.TryComplete();
                    // Don't leave a turn blocked on a permission prompt nobody will answer.
                    CancelPendingPermissions();
                    _turnCts.Cancel();
                    break;
                default:
                    EmitError("invalid_request", $"unknown command {cmd.T}");
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
                catch (Exception ex) { EmitError(ErrorCode(ex), ex.Message); }
            }
            _closed = true;
            _sends.Writer.TryComplete();
        }

        internal async Task RunAsync(CancellationToken outerCt)
        {
            Emit(new { t = "ready", session = _ephemeral ? null : _sessionConfig.Id, file = string.IsNullOrEmpty(_sessionConfig.FilePath) ? null : Path.GetFullPath(_sessionConfig.FilePath), model = ResolvedModel(), provider = _sessionConfig.Provider, mode = ModeName(), resumed = Resumed, noTools = _noTools, ephemeral = _ephemeral });

            var stdin = ReadStdin(outerCt);
            try
            {
                while (!_closed && !outerCt.IsCancellationRequested)
                {
                    SendRequest request;
                    try { request = await _sends.Reader.ReadAsync(outerCt); }
                    catch (ChannelClosedException) { break; }
                    catch (OperationCanceledException) { break; }

                    ApplyPendingChanges();
                    if (request.UserMessage.Content.OfType<ImageContent>().Any() &&
                        !ModelCapabilities.SupportsVision(_sessionConfig.Provider, ResolvedModel()))
                    {
                        EmitError("unsupported_input", $"{ResolvedModel()} cannot read images");
                        continue;
                    }
                    await RunTurnAsync(request, outerCt);
                }
            }
            catch (OperationCanceledException) { /* process shutdown */ }
            catch (Exception ex)
            {
                Emit(new { t = "error", code = ErrorCode(ex), message = ex.Message, fatal = true });
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
        private async Task RunTurnAsync(SendRequest request, CancellationToken outerCt)
        {
            _turnCts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, _turnCts.Token);
            // An interrupt, close or SIGTERM must also release a turn waiting on a permission answer.
            using var releasePermissions = linked.Token.Register(CancelPendingPermissions);

            // The agent loop logs the prompt, assistant turns and tool results in order.
            // Inline screenshots are untrusted input. Keep their analysis turn generation-only so
            // image text can never induce a tool call; a following text-only turn may still act.
            var turnNoTools = IsToolFreeTurn(request.UserMessage, _noTools);
            Emit(new { t = "start", model = ResolvedModel(), noTools = turnNoTools });
            var streamed = new System.Text.StringBuilder();
            try
            {
                var result = await _reflection.RunAgentWithReflectionAsync(new AgentRunOptions
                {
                    Prompt = request.Text,
                    UserMessage = request.UserMessage,
                    Session = _sessionConfig,
                    History = _history,
                    Streaming = true,
                    OnText = text => { streamed.Append(text); Emit(new { t = "text", delta = text }); },
                    OnPermissionRequest = _auto ? null : RequestPermission,
                    OnToolCall = tc => Emit(new { t = "tool_start", id = tc.Id, name = tc.Name, input = tc.Input }),
                    OnToolResult = tr => Emit(new { t = "tool_end", id = tr.ToolUseId, status = tr.IsError ? "error" : "done", output = tr.Content }),
                    LogToSession = !_ephemeral,
                    NoTools = turnNoTools,
                }, linked.Token);

                _history = [.. result.Messages];
                if (result.StopReason == StopReason.Error)
                    EmitError(ErrorCode(result.Error ?? result.Text), result.Error ?? result.Text);
                Emit(new
                {
                    t = "turn_end",
                    status = result.StopReason == StopReason.Error ? "failed" : result.StopReason == StopReason.Deadline ? "interrupted" : "completed",
                    model = ResolvedModel(),
                    isError = result.StopReason == StopReason.Error,
                    message = result.Error,
                    turns = result.Turns,
                    costUsd = result.CostUsd,
                    input = result.Usage.InputTokens,
                    output = result.Usage.OutputTokens,
                    usage = new { input = result.Usage.InputTokens, output = result.Usage.OutputTokens },
                    text = result.Text,
                    stopReason = result.StopReason.ToString().ToLowerInvariant(),
                });
            }
            catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
            {
                CancelPendingPermissions();
                await EndFailedTurnAsync(request.UserMessage, streamed.ToString(), "(interrupted)");
                Emit(new { t = "turn_end", status = "interrupted", model = ResolvedModel(), interrupted = true, text = streamed.ToString(), stopReason = "interrupted", usage = new { input = 0, output = 0 } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CancelPendingPermissions();
                await EndFailedTurnAsync(request.UserMessage, streamed.ToString(), $"(turn failed: {ex.Message})");
                EmitError(ErrorCode(ex), ex.Message);
                Emit(new { t = "turn_end", status = "failed", model = ResolvedModel(), isError = true, text = streamed.ToString(), stopReason = "error", message = ex.Message, usage = new { input = 0, output = 0 } });
            }
        }

        /// <summary>Keep user/assistant alternation in memory and on disk after a turn that did not complete.</summary>
        private async Task EndFailedTurnAsync(Message userMessage, string partial, string note)
        {
            var reply = string.IsNullOrWhiteSpace(partial) ? note : $"{partial}\n\n{note}";
            // The loop already logged the prompt; only the closing assistant note is written here.
            _history.Add(userMessage);
            _history.Add(Message.FromText(MessageRole.Assistant, reply));
            try { if (!_ephemeral) await _sessionMgr.LogAssistantMessageAsync(_sessionConfig, reply); }
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
                var provider = ModelRouter.ChooseProvider(model, null, _sessionConfig.Provider, configuredIsExplicit: true);
                _sessionConfig = _sessionConfig with { Model = model, Provider = provider };
            }
            var mode = _pendingMode;
            if (mode != null)
            {
                _pendingMode = null;
                _auto = mode == "auto";
                _sessionConfig = _sessionConfig with { PermissionMode = mode switch { "auto" => "auto", "plan" => "plan", "ask-all" => "askall", _ => "default" } };
            }
        }

        private Task<bool> RequestPermission(ToolCall tc)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_permLock) _permissions[tc.Id] = tcs;
            Emit(new { t = "permission", id = tc.Id, name = tc.Name, input = tc.Input });
            return tcs.Task;
        }

        private string ResolvedModel()
        {
            var prefix = _sessionConfig.Model.IndexOf(':');
            return prefix > 0 && ModelRouter.BuiltinProviderIds.Contains(_sessionConfig.Model[..prefix])
                ? _sessionConfig.Model[(prefix + 1)..]
                : _sessionConfig.Model;
        }

        private string ModeName() => _auto ? "auto" : _sessionConfig.PermissionMode.ToLowerInvariant() switch
        {
            "plan" => "plan",
            "askall" or "ask-all" => "ask-all",
            _ => "ask"
        };

        private void EmitError(string code, string message) => Emit(new { t = "error", code, message });

        private static string ErrorCode(Exception ex) => ex switch
        {
            UnauthorizedAccessException => "auth",
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "rate_limited",
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => "auth",
            _ => "internal_error"
        };

        private static string ErrorCode(string message)
        {
            var lower = message.ToLowerInvariant();
            if (lower.Contains("429") || lower.Contains("rate limit") || lower.Contains("quota")) return "rate_limited";
            if (lower.Contains("401") || lower.Contains("403") || lower.Contains("unauthorized") || lower.Contains("authentication")) return "auth";
            return "provider_error";
        }
    }
}
