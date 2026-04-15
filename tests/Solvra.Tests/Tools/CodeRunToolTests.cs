#nullable enable

using System.Text.Json;
using Solvra.Security;
using Solvra.Tools;
using Xunit;

namespace Solvra.Tests.Tools;

public class CodeRunToolTests
{
    private static ToolExecutionContext MakeContext() => new(
        SessionId: "test",
        Cwd: Path.GetTempPath(),
        PlanMode: false,
        Env: new Dictionary<string, string>(),
        Session: new SessionInfo("test", "auto", new List<string>(), new List<string>()));

    [Fact]
    public async Task ExecutesBashScript()
    {
        var sandbox = new SandboxManager(new SandboxConfig(TimeoutMs: 10000, BlockDangerous: false));
        var tool = new CodeRunTool(sandbox);

        var input = JsonSerializer.SerializeToElement(new
        {
            language = "bash",
            code = "echo hello_from_bash"
        });

        var result = await tool.ExecuteAsync(input, MakeContext());
        Assert.False(result.IsError);
        Assert.Contains("hello_from_bash", result.Output);
    }

    [Fact]
    public async Task ExecutesPythonScript()
    {
        var sandbox = new SandboxManager(new SandboxConfig(TimeoutMs: 10000, BlockDangerous: false));
        var tool = new CodeRunTool(sandbox);

        var input = JsonSerializer.SerializeToElement(new
        {
            language = "python",
            code = "print(2 + 2)"
        });

        var result = await tool.ExecuteAsync(input, MakeContext());
        // May fail if python3 not installed, but the tool should either succeed or return error
        if (!result.IsError)
            Assert.Contains("4", result.Output);
    }

    [Fact]
    public async Task RejectsUnsupportedLanguage()
    {
        var sandbox = new SandboxManager(new SandboxConfig());
        var tool = new CodeRunTool(sandbox);

        var input = JsonSerializer.SerializeToElement(new
        {
            language = "cobol",
            code = "DISPLAY 'HELLO'"
        });

        var result = await tool.ExecuteAsync(input, MakeContext());
        Assert.True(result.IsError);
        Assert.Contains("Unsupported language", result.Output);
    }

    [Fact]
    public void ToolMetadata()
    {
        var sandbox = new SandboxManager(new SandboxConfig());
        var tool = new CodeRunTool(sandbox);
        Assert.Equal("code_run", tool.Name);
        Assert.Equal(PermissionLevel.Execute, tool.PermissionLevel);
    }
}
