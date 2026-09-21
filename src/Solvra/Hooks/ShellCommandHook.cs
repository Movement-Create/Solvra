#nullable enable

using System.Diagnostics;
using System.Text.Json;

namespace Solvra.Hooks;

/// <summary>
/// A hook defined in config as a shell command (config "hooks": { "PreToolUse": ["cmd", ...] }).
/// The hook context is written to the command's stdin as JSON:
/// <c>{"event","session_id","turn","tool_name","tool_input","tool_output","is_error","final_text"}</c>.
/// Exit code 0 allows; exit code 2 blocks (stderr is the reason). On exit 0 the command may print
/// <c>{"action":"block","reason":"..."}</c> or <c>{"action":"modify","input":{...}}</c> to stdout.
/// Other exit codes and timeouts are reported on stderr and treated as allow, so a broken hook
/// never wedges the agent.
/// </summary>
public sealed class ShellCommandHook : IHook
{
    private readonly string _command;
    private readonly int _timeoutMs;

    public ShellCommandHook(HookEvent hookEvent, string command, int timeoutMs = 30_000)
    {
        Event = hookEvent;
        _command = command;
        _timeoutMs = timeoutMs;
        Id = $"{hookEvent}:{command}";
    }

    public string Id { get; }
    public HookEvent Event { get; }
    public string[]? ToolFilter => null;

    public async Task<HookResult> ExecuteAsync(HookContext context)
    {
        var payload = JsonSerializer.Serialize(new
        {
            @event = context.Event.ToString(),
            session_id = context.SessionId,
            turn = context.Turn,
            tool_name = context.ToolCall?.Name,
            tool_input = context.ToolCall?.Input,
            tool_output = context.ToolResult?.Output,
            is_error = context.ToolResult?.IsError,
            final_text = context.FinalText,
        });

        var psi = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(_command);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(payload);
            process.StandardInput.Close();

            using var cts = new CancellationTokenSource(_timeoutMs);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Console.Error.WriteLine($"[hook] '{_command}' timed out after {_timeoutMs} ms; ignoring");
                return new HookResult(HookAction.Allow);
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode == 2)
                return new HookResult(HookAction.Block, Reason: string.IsNullOrEmpty(stderr) ? $"Blocked by hook '{_command}'" : stderr);

            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"[hook] '{_command}' exited {process.ExitCode}: {stderr}");
                return new HookResult(HookAction.Allow);
            }

            return ParseDecision(stdout);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hook] '{_command}' failed: {ex.Message}");
            return new HookResult(HookAction.Allow);
        }
    }

    internal static HookResult ParseDecision(string stdout)
    {
        if (!stdout.StartsWith('{')) return new HookResult(HookAction.Allow);
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            return action?.ToLowerInvariant() switch
            {
                "block" => new HookResult(HookAction.Block, Reason: reason),
                "modify" when root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                    => new HookResult(HookAction.Modify, ModifiedInput: input.Clone(), Reason: reason),
                _ => new HookResult(HookAction.Allow)
            };
        }
        catch (JsonException)
        {
            return new HookResult(HookAction.Allow);
        }
    }

    /// <summary>Register every command hook from config on the engine.</summary>
    public static void RegisterFromConfig(HookEngine engine, Config.HooksConfig hooks)
    {
        foreach (var cmd in hooks.PreToolUse) engine.Register(new ShellCommandHook(HookEvent.PreToolUse, cmd));
        foreach (var cmd in hooks.PostToolUse) engine.Register(new ShellCommandHook(HookEvent.PostToolUse, cmd));
        foreach (var cmd in hooks.Stop) engine.Register(new ShellCommandHook(HookEvent.Stop, cmd));
    }
}
