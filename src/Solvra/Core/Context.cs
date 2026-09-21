using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Solvra.Models;

namespace Solvra.Core;

public static class Context
{
    private const int CharsPerToken = 4;
    private const double MicroCompactThreshold = 0.70;
    private const double TruncateThreshold = 0.85;
    private const double TruncateTarget = 0.50;
    private const int MicroCompactKeepChars = 2_000;
    private const int KeepRecentToolResults = 6;

    private static readonly Dictionary<string, int> ModelContextLimits = new()
    {
        ["claude-3-5-sonnet-20241022"] = 200_000,
        ["claude-3-5-haiku-20241022"] = 200_000,
        ["claude-3-opus-20240229"] = 200_000,
        ["claude-sonnet-4-20250514"] = 200_000,
        ["claude-haiku-4-5"] = 200_000,
        ["claude-sonnet-5"] = 1_000_000,
        ["claude-sonnet-4-6"] = 1_000_000,
        ["claude-opus-5"] = 1_000_000,
        ["claude-opus-4-8"] = 1_000_000,
        ["claude-opus-4-7"] = 1_000_000,
        ["claude-opus-4-6"] = 1_000_000,
        ["claude-fable-5"] = 1_000_000,
        ["gpt-4o"] = 128_000,
        ["gpt-4o-mini"] = 128_000,
        ["gpt-4.1"] = 1_000_000,
        ["gpt-5"] = 400_000,
        ["o1-preview"] = 128_000,
        ["o1"] = 200_000,
        ["o3"] = 200_000,
        ["o4-mini"] = 200_000,
        ["gemini-2.5-pro"] = 1_000_000,
        ["gemini-2.5-flash"] = 1_000_000,
        ["gemini-2.0-flash-lite"] = 1_000_000,
        ["llama3.1"] = 128_000,
        ["llama3.2"] = 128_000,
        ["llama3.1:70b"] = 128_000,
        // Common OpenAI-compatible gateway models (opencode Zen etc.)
        ["kimi-k2"] = 262_144,
        ["kimi-k3"] = 262_144,
        ["minimax-m2"] = 204_800,
        ["minimax-m3"] = 204_800,
        ["glm-5"] = 200_000,
        ["deepseek-v4"] = 128_000,
        ["qwen3"] = 262_144,
    };

    private const int DefaultContextLimit = 128_000;

    public static int EstimateTokens(string text) =>
        (int)Math.Ceiling((double)text.Length / CharsPerToken);

    public static int EstimateContextTokens(IReadOnlyList<Message> messages)
    {
        int total = 0;
        foreach (var msg in messages)
        {
            foreach (var block in msg.Content)
                total += EstimateTokens(BlockText(block));
        }
        return total;
    }

    private static string BlockText(MessageContent block) => block switch
    {
        TextContent tc => tc.Text,
        ToolUseContent tu => $"{tu.Name}({System.Text.Json.JsonSerializer.Serialize(tu.Input)})",
        ToolResultContent tr => tr.Content,
        ReasoningContent rc => rc.Text,
        _ => ""
    };

    public static int GetContextLimit(string model, string? provider = null)
    {
        var colon = model.IndexOf(':');
        if (colon > 0 && Providers.ModelRouter.BuiltinProviderIds.Contains(model[..colon]))
            model = model[(colon + 1)..];

        if (int.TryParse(Environment.GetEnvironmentVariable("SOLVRA_CONTEXT_LIMIT"), out var forced) && forced > 1000)
            return forced;

        if (ModelContextLimits.TryGetValue(model, out var limit))
            return limit;

        // Longest key that is a prefix of the model, then the old "first two dash parts" rule.
        var best = ModelContextLimits.Keys.Where(k => model.StartsWith(k, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length).FirstOrDefault();
        if (best != null) return ModelContextLimits[best];

        foreach (var (key, value) in ModelContextLimits)
        {
            var keyPrefix = string.Join("-", key.Split('-').Take(2));
            if (model.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return DefaultContextLimit;
    }

    // ------------------------------------------------------------------ system prompt

    public static string AssembleContext(
        string? basePrompt,
        string? solvraMarkdown,
        IReadOnlyList<string>? skills,
        IReadOnlyList<string>? lessons,
        string? memoryFacts,
        string? environment = null)
    {
        var parts = new List<string>();

        // A custom system prompt replaces the default instructions instead of being stacked on them.
        parts.Add(string.IsNullOrEmpty(basePrompt) ? DefaultInstructions : basePrompt);

        if (!string.IsNullOrEmpty(environment))
            parts.Add(environment);

        if (!string.IsNullOrEmpty(solvraMarkdown))
            parts.Add(solvraMarkdown);

        if (skills is { Count: > 0 })
            parts.Add("# Active Skills\n\n" + string.Join("\n\n---\n\n", skills));

        if (lessons is { Count: > 0 })
            parts.Add("# Lessons (relevant to this turn)\n\n" + string.Join("\n\n", lessons));

        if (!string.IsNullOrEmpty(memoryFacts))
            parts.Add("# Memory\n\n" + memoryFacts);

        return string.Join("\n\n", parts);
    }

    /// <summary>Instruction files read from the working directory up to the git root, outermost first.</summary>
    public static readonly string[] InstructionFileNames = ["AGENTS.md", "CLAUDE.md", "SOLVRA.md", ".solvra.md"];

    /// <summary>
    /// Collect project instructions: the user-global ~/.config/solvra/SOLVRA.md, then every
    /// AGENTS.md / CLAUDE.md / SOLVRA.md / .solvra.md from the git root (or filesystem root)
    /// down to <paramref name="cwd"/>. Each file is labelled with its path.
    /// </summary>
    public static async Task<string?> LoadProjectInstructionsAsync(string cwd, CancellationToken ct = default)
    {
        var files = new List<string>();

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var global = Path.Combine(configHome, "solvra", "SOLVRA.md");
        if (File.Exists(global)) files.Add(global);

        var dirs = new List<string>();
        for (var dir = new DirectoryInfo(cwd); dir != null; dir = dir.Parent)
        {
            dirs.Add(dir.FullName);
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                break;
        }
        // Outside a git repo only the cwd itself is consulted.
        if (!dirs.Any(d => Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"))))
            dirs = [dirs[0]];
        dirs.Reverse();

        foreach (var d in dirs)
            foreach (var name in InstructionFileNames)
            {
                var path = Path.Combine(d, name);
                if (File.Exists(path) && !files.Contains(path)) files.Add(path);
            }

        if (files.Count == 0) return null;

        var sb = new StringBuilder("# Project Instructions\n\nFollow these instructions from the user and the repository. More specific (deeper) files take precedence.\n");
        var budget = 40_000;
        foreach (var f in files)
        {
            string text;
            try { text = await File.ReadAllTextAsync(f, ct); } catch { continue; }
            if (text.Length > budget) text = text[..Math.Max(0, budget)] + "\n[truncated]";
            budget -= text.Length;
            sb.Append($"\n## {f}\n\n{text.Trim()}\n");
            if (budget <= 0) break;
        }
        return sb.ToString();
    }

    /// <summary>Environment facts the model otherwise has to guess: cwd, OS, shell, date, git state.</summary>
    public static string BuildEnvironmentInfo(string cwd, string model)
    {
        var sb = new StringBuilder("# Environment\n\n");
        sb.Append($"- Working directory: {cwd}\n");
        sb.Append($"- Platform: {RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})\n");
        sb.Append($"- Shell for the bash tool: /bin/bash\n");
        sb.Append($"- Today's date: {DateTime.Now:yyyy-MM-dd}\n");
        sb.Append($"- Model: {model}\n");

        var branch = Git(cwd, "rev-parse --abbrev-ref HEAD");
        if (branch != null)
        {
            sb.Append($"- Git repository: yes (branch {branch})\n");
            var status = Git(cwd, "status --porcelain");
            if (status != null)
            {
                var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                sb.Append(lines.Length == 0 ? "- Git status: clean\n" : $"- Git status: {lines.Length} changed/untracked file(s)\n");
                foreach (var l in lines.Take(20)) sb.Append($"    {l}\n");
                if (lines.Length > 20) sb.Append($"    ... {lines.Length - 20} more\n");
            }
            var log = Git(cwd, "log --oneline -5");
            if (!string.IsNullOrWhiteSpace(log))
                sb.Append("- Recent commits:\n").Append(string.Join("", log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => $"    {l}\n")));
        }
        else
        {
            sb.Append("- Git repository: no\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string? Git(string cwd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output.TrimEnd() : null;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ compression

    /// <summary>
    /// Keep the request inside the model's context window.
    /// <paramref name="overheadTokens"/> covers the system prompt and tool schemas.
    /// Above 70%: older tool results are shortened (head+tail kept; the latest few stay intact).
    /// Above 85% (or still too big): whole old exchanges are dropped, cutting only at user-turn
    /// boundaries so a tool call is never separated from its result.
    /// </summary>
    public static IReadOnlyList<Message> CompressContext(
        IReadOnlyList<Message> messages,
        string model,
        string? provider = null,
        int overheadTokens = 0)
    {
        var contextLimit = GetContextLimit(model, provider);
        var estimatedTokens = EstimateContextTokens(messages) + overheadTokens;
        var ratio = (double)estimatedTokens / contextLimit;

        if (ratio < MicroCompactThreshold)
            return messages;

        var compacted = MicroCompact(messages);
        var afterCompact = EstimateContextTokens(compacted) + overheadTokens;
        if ((double)afterCompact / contextLimit < TruncateThreshold)
            return compacted;

        var budget = (int)(contextLimit * TruncateTarget) - overheadTokens;
        return TruncateOld(compacted, Math.Max(budget, contextLimit / 10));
    }

    private static IReadOnlyList<Message> MicroCompact(IReadOnlyList<Message> messages)
    {
        // Leave the most recent tool results intact: the model is usually working from them.
        var toolIdx = messages.Select((m, i) => (m, i)).Where(x => x.m.Role == MessageRole.Tool).Select(x => x.i).ToList();
        // Tool messages before this index are "old"; with few tool results none are.
        var protectedFrom = toolIdx.Count > KeepRecentToolResults ? toolIdx[^KeepRecentToolResults] : 0;
        // Recent results are only shortened when a single one is itself huge.
        var hugeLimit = MicroCompactKeepChars * 20;

        var result = new List<Message>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role != MessageRole.Tool)
            {
                result.Add(msg);
                continue;
            }

            var isOld = i < protectedFrom;
            var newContent = new List<MessageContent>();
            var changed = false;
            foreach (var block in msg.Content)
            {
                if (block is ToolResultContent tr &&
                    ((isOld && tr.Content.Length > MicroCompactKeepChars) || tr.Content.Length > hugeLimit))
                {
                    var keep = isOld ? MicroCompactKeepChars : hugeLimit;
                    newContent.Add(tr with
                    {
                        Content = tr.Content[..(keep * 2 / 3)] +
                            $"\n...[compacted: {tr.Content.Length} chars total; re-run the tool if you need the full output]...\n" +
                            tr.Content[^(keep / 3)..]
                    });
                    changed = true;
                }
                else
                {
                    newContent.Add(block);
                }
            }
            result.Add(changed ? msg with { Content = newContent } : msg);
        }
        return result;
    }

    /// <summary>A user message with real text (not tool results) starts a new exchange.</summary>
    private static bool IsExchangeStart(Message m) =>
        m.Role == MessageRole.User && m.Content.Any(c => c is TextContent or ImageContent);

    private static IReadOnlyList<Message> TruncateOld(IReadOnlyList<Message> messages, int tokenBudget)
    {
        if (messages.Count <= 3) return messages;

        // Always keep the first exchange's opening user message: it usually states the task.
        var first = messages[0];
        var firstTokens = EstimateContextTokens([first]);

        // Walk back from the end until the budget is used, then move the cut forward to the
        // next exchange start so no tool_use/tool_result pair is split.
        var used = firstTokens;
        var cut = messages.Count;
        for (var i = messages.Count - 1; i >= 1; i--)
        {
            var t = EstimateContextTokens([messages[i]]);
            if (used + t > tokenBudget) break;
            used += t;
            cut = i;
        }

        var start = cut;
        while (start < messages.Count && !IsExchangeStart(messages[start])) start++;

        if (start >= messages.Count)
        {
            // The current exchange alone exceeds the budget: keep it whole from its own start
            // (we cannot drop part of it without breaking tool pairing); MicroCompact has
            // already shortened what it could.
            start = messages.Count - 1;
            while (start > 1 && !IsExchangeStart(messages[start])) start--;
        }
        if (start <= 1) return messages;

        var dropped = start - 1;
        var result = new List<Message>(messages.Count - dropped + 2) { first };
        result.Add(Message.FromText(MessageRole.Assistant,
            $"[Context compacted: {dropped} earlier messages were removed to fit the context window. " +
            "Re-read files or re-run commands if you need details from that part of the conversation.]"));
        for (var i = start; i < messages.Count; i++)
            result.Add(messages[i]);
        return result;
    }

    private const string DefaultInstructions = """
        # Agent Instructions

        You are Solvra, an autonomous coding agent working in the user's project through tools.

        How to work:
        1. Understand before changing: locate the relevant code with grep/glob and read it with file_read.
           Page through large files with offset/limit; never cat or print huge files or logs whole.
        2. Make focused changes. Use file_edit for existing files (old_string must match exactly and be
           unique; read the file first). Use file_write only for new files or full rewrites.
        3. Follow the project's conventions and any Project Instructions below. Don't add unrelated changes.
        4. Verify: run the relevant tests, build or linter with bash and fix what you broke. Report honestly
           if something could not be verified.
        5. For multi-step work, keep a short plan with the todo tool and update it as you go.
        6. When a tool fails, read the error and change approach instead of repeating the same call.
        7. Never run destructive commands on data outside the task (deleting home or system paths, force
           pushes, resetting uncommitted work). Don't expose secrets in output.
        8. Finish with a brief summary: what changed (files), how it was verified, and anything left open.
        """;
}
