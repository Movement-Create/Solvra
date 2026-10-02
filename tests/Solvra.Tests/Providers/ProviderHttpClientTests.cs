using Solvra.Providers;

namespace Solvra.Tests.Providers;

public class ProviderHttpClientTests
{
    [Fact]
    public void DefaultTimeout_IsLongerThanDotNetDefault()
    {
        using var client = ProviderHttpClient.Create(ProviderHttpClient.DefaultTimeoutSeconds);
        Assert.Equal(TimeSpan.FromMinutes(10), client.Timeout);
    }

    [Fact]
    public void Zero_DisablesPerRequestTimeout()
    {
        using var client = ProviderHttpClient.Create(0);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86_401)]
    public void InvalidTimeout_IsRejected(int seconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ProviderHttpClient.Create(seconds));
        Assert.Contains("Model timeout", ex.Message);
    }

    [Fact]
    public void Router_PropagatesTimeoutToBuiltInProviders()
    {
        var router = new ModelRouter(-1);
        Assert.Throws<ArgumentOutOfRangeException>(() => router.GetProvider("openai"));
    }

    [Fact]
    public void EffortParsing_IsStrictAndCompatible()
    {
        Assert.Equal(Solvra.Models.EffortLevel.ExtraHigh, Solvra.Models.EffortLevelExtensions.Parse("xhigh"));
        Assert.Equal(Solvra.Models.EffortLevel.ExtraHigh, Solvra.Models.EffortLevelExtensions.Parse("max"));
        Assert.Equal("xhigh", Solvra.Models.EffortLevelExtensions.ToWireString(Solvra.Models.EffortLevel.ExtraHigh));
        Assert.Throws<ArgumentException>(() => Solvra.Models.EffortLevelExtensions.Parse("huge"));
    }

    [Fact]
    public async Task ConfigFile_SetsSubagentDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"solvra-subagents-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{"subagents":"off","subagent_model":"openai:gpt-4.1-mini","subagent_effort":"low"}""");
        try
        {
            var config = await Solvra.Config.ConfigLoader.LoadAsync(path);
            Assert.False(config.SubagentsEnabled);
            Assert.Equal("openai:gpt-4.1-mini", config.SubagentModel);
            Assert.Equal(Solvra.Models.EffortLevel.Low, config.ParsedSubagentEffort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConfigFile_SetsModelTimeout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"solvra-timeout-{Guid.NewGuid():N}.json");
        var previous = Environment.GetEnvironmentVariable("SOLVRA_MODEL_TIMEOUT_SECONDS");
        await File.WriteAllTextAsync(path, """{"model_timeout_seconds":123}""");
        try
        {
            Environment.SetEnvironmentVariable("SOLVRA_MODEL_TIMEOUT_SECONDS", null);
            var config = await Solvra.Config.ConfigLoader.LoadAsync(path);
            Assert.Equal(123, config.ModelTimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOLVRA_MODEL_TIMEOUT_SECONDS", previous);
            File.Delete(path);
        }
    }
}
