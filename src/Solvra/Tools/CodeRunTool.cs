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
            timeout_ms = new { type = "integer", description = "Timeout in milliseconds", @default = 30000 }
        },
        required = new[] { "language", "code" }
    });

    public override async Task<ToolExecuteResult> ExecuteAsync(JsonElement input, ToolExecutionContext context, CancellationToken ct = default)
    {
        var language = GetString(input, "language").ToLowerInvariant();
        var code = GetString(input, "code");
        var timeoutMs = GetInt(input, "timeout_ms", 30000);

        if (!Languages.TryGetValue(language, out var langInfo))
            return new ToolExecuteResult($"Unsupported language: {language}. Supported: {string.Join(", ", Languages.Keys)}", true);

        var filename = GetOptionalString(input, "filename") ?? $"script_{Guid.NewGuid():N}{langInfo.Extension}";
        var tempDir = Path.Combine(Path.GetTempPath(), $"solvra-code-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var filePath = Path.Combine(tempDir, filename);
        await File.WriteAllTextAsync(filePath, code, ct);

        var sandbox = new SandboxManager(new SandboxConfig(TimeoutMs: timeoutMs, BlockDangerous: false));
        var result = await sandbox.ExecAsync($"{langInfo.Interpreter} {filePath}", tempDir, context.Env, ct);

        // Collect generated files
        var generatedFiles = Directory.GetFiles(tempDir)
            .Where(f => f != filePath)
            .Select(f => Path.GetFileName(f))
            .ToList();

        var output = result.Stdout;
        if (!string.IsNullOrEmpty(result.Stderr))
            output += (string.IsNullOrEmpty(output) ? "" : "\n") + result.Stderr;

        if (generatedFiles.Count > 0)
            output += $"\n\n[Generated files: {string.Join(", ", generatedFiles)}]";

        if (result.TimedOut)
            output = $"Execution timed out after {timeoutMs}ms\n{output}";

        return new ToolExecuteResult(output, result.ExitCode != 0 || result.TimedOut);
    }
}
