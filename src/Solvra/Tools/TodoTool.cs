#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class TodoTool : ToolBase
{
    // One list per session: subagents, parallel webhook jobs and chat sessions no longer share it.
    private static readonly Dictionary<string, List<TodoItem>> Lists = new();
    private static readonly object Lock = new();
    [ThreadStatic] private static List<TodoItem>? _items;
    private static List<TodoItem> Items => _items!;

    public override string Name => "todo";
    public override string Description =>
        "Track the steps of a multi-step task. Actions: set (replace the whole list with tasks[]), add (task), " +
        "update (index, status: pending|in_progress|done), list, clear.";
    // Harness-internal state only (no user files), so it needs no approval.
    public override PermissionLevel PermissionLevel => PermissionLevel.Read;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            action = new { type = "string", @enum = new[] { "set", "add", "update", "list", "clear" }, description = "Action" },
            tasks = new { type = "array", items = new { type = "string" }, description = "Full task list (for set)" },
            task = new { type = "string", description = "Task description (for add)" },
            index = new { type = "integer", description = "Task index (for update, 0-based)" },
            status = new { type = "string", description = "New status: pending, in_progress, done (for update)" }
        },
        required = new[] { "action" }
    });

    public override Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var action = GetString(input, "action").ToLowerInvariant();

        lock (Lock)
        {
            if (!Lists.TryGetValue(context.SessionId, out var list))
                Lists[context.SessionId] = list = new List<TodoItem>();
            _items = list;
            try
            {
                return Task.FromResult(action switch
                {
                    "set" => SetTasks(input),
                    "add" => AddTask(input),
                    "update" => UpdateTask(input),
                    "list" => ListTasks(),
                    "clear" => ClearTasks(),
                    _ => new ToolExecuteResult($"Unknown action: {action}. Use set, add, update, list or clear.", true)
                });
            }
            finally
            {
                _items = null;
            }
        }
    }

    private static ToolExecuteResult SetTasks(JsonElement input)
    {
        var tasks = GetStringArray(input, "tasks").Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (tasks.Count == 0)
            return new ToolExecuteResult("Error: tasks[] is required for set", true);
        Items.Clear();
        Items.AddRange(tasks.Select(t => new TodoItem(t, "pending")));
        return ListTasks();
    }

    private static ToolExecuteResult ClearTasks()
    {
        Items.Clear();
        return new ToolExecuteResult("Cleared.", false);
    }

    private static ToolExecuteResult AddTask(JsonElement input)
    {
        var task = input.TryGetProperty("task", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(task))
            return new ToolExecuteResult("Error: task is required for add", true);

        Items.Add(new TodoItem(task, "pending"));
        return new ToolExecuteResult($"Added task [{Items.Count - 1}]: {task}", false);
    }

    private static ToolExecuteResult UpdateTask(JsonElement input)
    {
        var index = input.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
        if (index < 0 || index >= Items.Count)
            return new ToolExecuteResult($"Error: invalid index {index}. Valid range: 0-{Items.Count - 1}", true);

        var status = input.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "pending" : "pending";
        if (status is not ("pending" or "in_progress" or "done"))
            return new ToolExecuteResult("Error: status must be pending, in_progress or done", true);
        Items[index] = Items[index] with { Status = status };
        return new ToolExecuteResult($"Updated task [{index}]: {Items[index].Task} → {status}", false);
    }

    private static ToolExecuteResult ListTasks()
    {
        if (Items.Count == 0)
            return new ToolExecuteResult("No tasks.", false);

        var lines = Items.Select((item, i) =>
        {
            var icon = item.Status switch
            {
                "done" => "[x]",
                "in_progress" => "[~]",
                _ => "[ ]"
            };
            return $"{i}. {icon} {item.Task}";
        });

        return new ToolExecuteResult(string.Join('\n', lines), false);
    }

    private record TodoItem(string Task, string Status);
}
