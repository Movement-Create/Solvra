#nullable enable

using System.Text.Json;

namespace Solvra.Hooks;

public class HookEngine
{
    private readonly List<IHook> _hooks = new();

    public void Register(IHook hook)
    {
        _hooks.Add(hook);
    }

    public void Unregister(string hookId)
    {
        _hooks.RemoveAll(h => h.Id == hookId);
    }

    public IReadOnlyList<IHook> GetHooks() => _hooks.AsReadOnly();

    public async Task<HookResult> FirePreToolUseAsync(string sessionId, int turn, ToolCallInfo toolCall)
    {
        var context = new HookContext(
            Event: HookEvent.PreToolUse,
            SessionId: sessionId,
            Turn: turn,
            ToolCall: toolCall);

        return await RunHooksAsync(context, HookEvent.PreToolUse, toolCall.Name);
    }

    public async Task<HookResult> FirePostToolUseAsync(
        string sessionId, int turn, ToolCallInfo toolCall, ToolResultInfo toolResult)
    {
        var context = new HookContext(
            Event: HookEvent.PostToolUse,
            SessionId: sessionId,
            Turn: turn,
            ToolCall: toolCall,
            ToolResult: toolResult);

        return await RunHooksAsync(context, HookEvent.PostToolUse, toolCall.Name);
    }

    public async Task<HookResult> FireStopAsync(string sessionId, int turn, string finalText)
    {
        var context = new HookContext(
            Event: HookEvent.Stop,
            SessionId: sessionId,
            Turn: turn,
            FinalText: finalText);

        return await RunHooksAsync(context, HookEvent.Stop, null);
    }

    private async Task<HookResult> RunHooksAsync(HookContext context, HookEvent eventType, string? toolName)
    {
        var relevant = _hooks.Where(h =>
        {
            if (h.Event != eventType) return false;

            // If hook has a tool filter and we have a tool name, check it
            if (h.ToolFilter != null && toolName != null)
                return h.ToolFilter.Contains(toolName, StringComparer.OrdinalIgnoreCase);

            return true;
        });

        foreach (var hook in relevant)
        {
            try
            {
                var result = await hook.ExecuteAsync(context);

                if (result.Action == HookAction.Block)
                    return result;

                if (result.Action == HookAction.Modify)
                    return result;

                // Action == Allow: continue to next hook
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Hook {hook.Id}] Error: {ex.Message}");
                // Hook error: log and continue
            }
        }

        return new HookResult(HookAction.Allow);
    }
}
