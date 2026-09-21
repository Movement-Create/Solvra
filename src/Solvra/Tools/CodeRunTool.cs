#nullable enable

using System.Text.Json;
using Solvra.Security;

namespace Solvra.Tools;

public class CodeRunTool : ToolBase
{
    private readonly SandboxManager _sandbox;

    public CodeRunTool(SandboxManager sandbox)
    {
        _sandbox = sandbox;
    }

    public override string Name => "code_run";
    public override string Description => "Write code to a temp file and execute it with the appropriate interpreter.";
    public override PermissionLevel PermissionLevel => PermissionLevel.Execute;

    private static readonly Dictionary<string, (string Extension, string Interpreter)> Languages = new()
    {
        ["python"] = (".py", "python3"),
        ["node"] = (".js", "node"),
        ["bash"] = (".sh", "bash"),
        ["typescript"] = (".ts", "npx ts-node"),
    };

    public override JsonElement GetInputSchema() => BuildSchema(new
    {
        type = "object",
        properties = new
        {
            language = new { type = "string", description = "Language: python, node, bash, typescript" },
            code = new { type = "string", description = "Source code to execute" },
            filename = new { type = "string", description = "Optional filename" },
            timeout_ms = new { type = "integer", description = "Timeout in milliseconds (default 120000, max 600000)" }
        },
        required = new[] { "language", "code" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var language = GetString(input, "language").ToLowerInvariant();
        var code = GetString(input, "code");
        var timeoutMs = Math.Clamp(GetInt(input, "timeout_ms", 120_000), 1, SandboxConfig.MaxTimeoutMs);

        if (!Languages.TryGetValue(language, out var langInfo))
            return new ToolExecuteResult($"Unsupported language: {language}. Supported: {string.Join(", ", Languages.Keys)}", true);

        // Only a bare file name is accepted: "../x" or "/etc/x" must not escape the temp dir.
        var requested = GetOptionalString(input, "filename");
        var filename = string.IsNullOrWhiteSpace(requested) ? "" : Path.GetFileName(requested);
        if (string.IsNullOrWhiteSpace(filename) || filename is "." or "..")
            filename = $"script_{Guid.NewGuid():N}{langInfo.Extension}";

        if (language is "bash" or "sh" && new DangerousCommandDetector().Detect(code) is { Dangerous: true } danger)
            return new ToolExecuteResult($"[Blocked] {danger.Reason}. The script was not run.", true);
        var tempDir = Path.Combine(Path.GetTempPath(), $"solvra-code-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var filePath = Path.Combine(tempDir, filename);
        await File.WriteAllTextAsync(filePath, code, ct);

        // The interpreter line itself is harmless; the script content was screened above.
        var sandbox = new SandboxManager(_sandbox.Config with { BlockDangerous = false });
        var result = await sandbox.ExecAsync($"{langInfo.Interpreter} '{filePath}'", tempDir, context.Env, ct, timeoutMs);

        // Collect generated files
        var generatedFiles = Directory.GetFiles(tempDir)
            .Where(f => f != filePath)
            .Select(f => Path.GetFileName(f))
            .ToList();

        var output = result.Stdout;
        if (!string.IsNullOrEmpty(result.Stderr))
            output += (string.IsNullOrEmpty(output) ? "" : "\n") + result.Stderr;

        output = ToolOutput.Limit(output, "code_run");

        if (generatedFiles.Count > 0)
            output += $"\n\n[Generated files in {tempDir}: {string.Join(", ", generatedFiles)}]";
        else
            TryDelete(tempDir);

        if (result.TimedOut)
            output = $"Execution timed out after {timeoutMs}ms\n{output}";

        return new ToolExecuteResult(output, result.ExitCode != 0 || result.TimedOut);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
