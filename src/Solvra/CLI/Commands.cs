#nullable enable

using Solvra.Config;
using Solvra.Core;
using Solvra.Memory;
using Solvra.Observability;
using Solvra.Scheduler;
using Solvra.Skills;
using Solvra.Tools;

namespace Solvra.CLI;

/// <summary>
/// CLI command implementations for de-stubbed and new commands (Fixes 6a-6c).
/// These are extracted so Program.cs can call them without cluttering the wiring.
/// </summary>
public static class Commands
{
    /// <summary>
    /// Fix 6a: De-stubbed serve command implementation.
    /// Starts WebhookServer and CronScheduler concurrently.
    /// </summary>
    public static async Task ServeAsync(SolvraConfig config, int port, bool noCron, bool noWebhook, CancellationToken ct)
    {
        var tasks = new List<Task>();

        if (!noWebhook)
        {
            var webhookServer = new WebhookServer(port, config.WebhookSecret);
            Printer.Info($"Webhook server starting on port {port}");
            webhookServer.Start();
            tasks.Add(Task.Delay(Timeout.Infinite, ct));
        }

        if (!noCron)
        {
            var cronScheduler = new CronScheduler();
            foreach (var job in config.Cron.Where(j => j.Enabled))
            {
                cronScheduler.AddJob(job);
                Printer.Info($"Cron job registered: {job.Name} ({job.Schedule})");
            }

            if (cronScheduler.ListJobs().Count > 0)
            {
                Printer.Info("Cron scheduler starting");
                tasks.Add(cronScheduler.StartAsync(ct));
            }
        }

        if (tasks.Count == 0)
        {
            Printer.Warn("Both webhook and cron are disabled. Nothing to do.");
            return;
        }

        Printer.Success("Solvra serve is running. Press Ctrl+C to stop.");
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Fix 6b: De-stubbed memory prune command.
    /// </summary>
    public static async Task MemoryPruneAsync(string memoryDir, int daysOld = 30, bool force = false)
    {
        var memoryManager = new MemoryManager(memoryDir);
        var lessons = await memoryManager.ParseLessonsAsync();

        if (lessons.Count == 0)
        {
            Console.WriteLine("No lessons found.");
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-daysOld);
        var oldLessons = lessons.Where(l =>
            DateTime.TryParse(l.Date, out var d) && d < cutoff).ToList();

        if (oldLessons.Count == 0)
        {
            Console.WriteLine($"No lessons older than {daysOld} days.");
            return;
        }

        Console.WriteLine($"Found {oldLessons.Count} lessons older than {daysOld} days (out of {lessons.Count} total).");

        if (!force)
        {
            Console.Write("Remove them? (y/n): ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes"))
            {
                Console.WriteLine("Cancelled.");
                return;
            }
        }

        var remaining = lessons.Where(l => !oldLessons.Contains(l)).ToList();
        await memoryManager.WriteLessonsAsync(remaining);
        Console.WriteLine($"Removed {oldLessons.Count} lessons. {remaining.Count} remaining.");
    }

    /// <summary>
    /// Fix 6c: memory add command.
    /// </summary>
    public static async Task MemoryAddAsync(string memoryDir, string content)
    {
        var memoryManager = new MemoryManager(memoryDir);
        await memoryManager.AppendFactAsync(content);
        Console.WriteLine("Fact added to memory.");
    }

    /// <summary>
    /// Fix 6c: memory search command.
    /// </summary>
    public static async Task MemorySearchAsync(string memoryDir, string query)
    {
        var memoryManager = new MemoryManager(memoryDir);
        var results = await memoryManager.SearchAsync(query);

        if (string.IsNullOrEmpty(results))
        {
            Console.WriteLine("No results found.");
            return;
        }

        Console.WriteLine(results);
    }

    /// <summary>
    /// Fix 6c: tools command - list all registered tools.
    /// </summary>
    public static void ListTools(ToolRegistry registry)
    {
        Console.WriteLine("Available tools:\n");
        foreach (var tool in registry.GetAllTools())
        {
            Console.WriteLine($"  {tool.Name,-25} {tool.Description}");
        }
    }

    /// <summary>
    /// Fix 6c: skills command - list all discovered skills.
    /// </summary>
    public static async Task ListSkillsAsync(string skillsDir)
    {
        var loader = new SkillLoader(skillsDir);
        var skills = await loader.GetAllSkillsAsync();

        if (skills.Count == 0)
        {
            Console.WriteLine("No skills found.");
            return;
        }

        Console.WriteLine("Available skills:\n");
        foreach (var skill in skills)
        {
            var triggers = skill.TriggerPatterns.Count > 0
                ? $" [triggers: {string.Join(", ", skill.TriggerPatterns)}]"
                : "";
            var inject = skill.AlwaysInject ? " [always-inject]" : "";
            Console.WriteLine($"  {skill.Name,-25} {skill.Description}{triggers}{inject}");
        }
    }

    /// <summary>
    /// Fix 6c: session resume command.
    /// </summary>
    public static async Task SessionResumeAsync(string sessionsDir, string sessionId)
    {
        var mgr = new SessionManager(sessionsDir);
        try
        {
            var info = await mgr.ResumeAsync(sessionId);
            Console.WriteLine($"Session {sessionId} loaded with {info.Messages.Count} messages.");
            Console.WriteLine($"Model: {info.Config.Model}");
            Console.WriteLine($"Created: {info.Config.CreatedAt}");
            Console.WriteLine("\nTo continue this session, use: solvra chat --resume {sessionId}");
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"Session not found: {sessionId}");
        }
    }

    /// <summary>
    /// Fix 6c: session delete command.
    /// </summary>
    public static async Task SessionDeleteAsync(string sessionsDir, string sessionId)
    {
        var mgr = new SessionManager(sessionsDir);
        await mgr.DeleteAsync(sessionId);
        Console.WriteLine($"Session {sessionId} deleted.");
    }
}
