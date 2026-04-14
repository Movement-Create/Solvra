#nullable enable

using Solvra.Security;
using Xunit;

namespace Solvra.Tests.Security;

public class SandboxTests
{
    [Fact]
    public async Task ExecutesCommandSuccessfully()
    {
        var sandbox = new SandboxManager();
        var result = await sandbox.ExecAsync("echo hello");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Stdout);
        Assert.False(result.Blocked);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task EnforcesTimeout()
    {
        var sandbox = new SandboxManager(new SandboxConfig(TimeoutMs: 1000));
        var result = await sandbox.ExecAsync("sleep 30");

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task BlocksDangerousCommands()
    {
        var sandbox = new SandboxManager();
        var result = await sandbox.ExecAsync("rm -rf /");

        Assert.True(result.Blocked);
        Assert.NotNull(result.BlockReason);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task RespectsWorkingDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sandbox_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sandbox = new SandboxManager();
            var result = await sandbox.ExecAsync("pwd", tempDir);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(tempDir, result.Stdout);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RestrictsDirectory()
    {
        var allowedDir = Path.Combine(Path.GetTempPath(), $"sandbox_allowed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(allowedDir);
        try
        {
            var sandbox = new SandboxManager(new SandboxConfig(AllowedDirectory: allowedDir));
            var result = await sandbox.ExecAsync("echo test", "/etc");

            Assert.True(result.Blocked);
            Assert.Contains("outside allowed directory", result.Stderr);
        }
        finally
        {
            Directory.Delete(allowedDir, true);
        }
    }

    [Fact]
    public async Task CapturesStderr()
    {
        var sandbox = new SandboxManager();
        var result = await sandbox.ExecAsync("echo error >&2");

        Assert.Contains("error", result.Stderr);
    }

    [Fact]
    public async Task PassesEnvironmentVariables()
    {
        var sandbox = new SandboxManager();
        var env = new Dictionary<string, string> { ["MY_TEST_VAR"] = "test_value_123" };
        var result = await sandbox.ExecAsync("echo $MY_TEST_VAR", extraEnv: env);

        Assert.Contains("test_value_123", result.Stdout);
    }
}
