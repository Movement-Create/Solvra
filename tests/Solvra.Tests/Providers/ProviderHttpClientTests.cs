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
