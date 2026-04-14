#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class TodoTool : ToolBase
{
    private static readonly List<TodoItem> Items = new();
    private static readonly object Lock = new();

    public override string Name => "todo";
    public override string Description => "Track tasks: add, update status, or list.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Write;

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            action = new { type = "string", description = "Action: add, update, list" },
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
            return Task.FromResult(action switch
            {
                "add" => AddTask(input),
                "update" => UpdateTask(input),
                "list" => ListTasks(),
                _ => new ToolExecuteResult($"Unknown action: {action}. Use add, update, or list.", true)
            });
        }
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
        var index = input.TryGetProperty("index", out var i) ? i.GetInt32() : -1;
        if (index < 0 || index >= Items.Count)
            return new ToolExecuteResult($"Error: invalid index {index}. Valid range: 0-{Items.Count - 1}", true);

        var status = input.TryGetProperty("status", out var s) ? s.GetString() ?? "pending" : "pending";
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
