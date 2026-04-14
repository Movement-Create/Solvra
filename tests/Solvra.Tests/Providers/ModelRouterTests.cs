using Moq;
using Xunit;
using Solvra.Models;
using Solvra.Providers;

namespace Solvra.Tests.Providers;

public class ModelRouterTests
{
    private ModelRouter CreateRouter(Dictionary<string, Mock<IProvider>>? mocks = null)
    {
        var factories = new Dictionary<string, Func<IProvider>>();
        if (mocks != null)
        {
            foreach (var (id, mock) in mocks)
            {
                mock.SetupGet(p => p.Id).Returns(id);
                factories[id] = () => mock.Object;
            }
        }
        else
        {
            // Use defaults with mocked providers
            foreach (var id in new[] { "anthropic", "openai", "google", "ollama" })
            {
                var m = new Mock<IProvider>();
                m.SetupGet(p => p.Id).Returns(id);
                factories[id] = () => m.Object;
            }
        }
        return new ModelRouter(factories);
    }

    [Theory]
    [InlineData("claude-3-5-sonnet-20241022", "anthropic")]
    [InlineData("claude-3-opus-20240229", "anthropic")]
    [InlineData("gpt-4o", "openai")]
    [InlineData("gpt-4o-mini", "openai")]
    [InlineData("o1-preview", "openai")]
    [InlineData("gemini-2.5-pro", "google")]
    [InlineData("gemini-2.0-flash", "google")]
    [InlineData("llama3.1", "ollama")]
    [InlineData("mistral-7b", "ollama")]
    [InlineData("phi-3", "ollama")]
    public void DetectProvider_CorrectlyMapsModelPrefixes(string model, string expectedProvider)
    {
        Assert.Equal(expectedProvider, ModelRouter.DetectProvider(model));
    }

    [Fact]
    public void DetectProvider_ReturnsNullForUnknownModel()
    {
        Assert.Null(ModelRouter.DetectProvider("unknown-model"));
    }

    [Fact]
    public void Resolve_ExplicitProviderSyntax()
    {
        var router = CreateRouter();
        var (provider, model) = router.Resolve("anthropic:claude-3-5-sonnet-20241022");

        Assert.Equal("anthropic", provider.Id);
        Assert.Equal("claude-3-5-sonnet-20241022", model);
    }

    [Fact]
    public void Resolve_AutoDetectsFromPrefix()
    {
        var router = CreateRouter();
        var (provider, model) = router.Resolve("gpt-4o");

        Assert.Equal("openai", provider.Id);
        Assert.Equal("gpt-4o", model);
    }

    [Fact]
    public void Resolve_UsesDefaultProviderForUnknownModel()
    {
        var router = CreateRouter();
        var (provider, model) = router.Resolve("custom-model", "openai");

        Assert.Equal("openai", provider.Id);
        Assert.Equal("custom-model", model);
    }

    [Fact]
    public void Resolve_FallsBackToAnthropicByDefault()
    {
        var router = CreateRouter();
        var (provider, model) = router.Resolve("custom-model");

        Assert.Equal("anthropic", provider.Id);
        Assert.Equal("custom-model", model);
    }

    [Fact]
    public void Resolve_ThrowsForUnknownProvider()
    {
        var router = CreateRouter();
        Assert.Throws<ArgumentException>(() => router.Resolve("nonexistent:model"));
    }

    [Fact]
    public void GetProvider_CachesInstances()
    {
        var router = CreateRouter();
        var p1 = router.GetProvider("anthropic");
        var p2 = router.GetProvider("anthropic");

        Assert.Same(p1, p2);
    }

    [Theory]
    [InlineData("anthropic", EffortLevel.Low, "claude-3-5-haiku-20241022")]
    [InlineData("anthropic", EffortLevel.Medium, "claude-3-5-sonnet-20241022")]
    [InlineData("anthropic", EffortLevel.Max, "claude-3-opus-20240229")]
    [InlineData("openai", EffortLevel.Low, "gpt-4o-mini")]
    [InlineData("openai", EffortLevel.Max, "o1-preview")]
    [InlineData("google", EffortLevel.Low, "gemini-2.0-flash-lite")]
    [InlineData("google", EffortLevel.Max, "gemini-2.5-pro")]
    [InlineData("ollama", EffortLevel.Low, "llama3.2")]
    [InlineData("ollama", EffortLevel.Max, "llama3.1:405b")]
    public void GetEffortModel_ReturnsCorrectModel(string provider, EffortLevel effort, string expected)
    {
        Assert.Equal(expected, ModelRouter.GetEffortModel(provider, effort));
    }

    [Fact]
    public async Task AutoSelectAsync_ReturnsFirstValidProvider()
    {
        var anthropic = new Mock<IProvider>();
        anthropic.SetupGet(p => p.Id).Returns("anthropic");
        anthropic.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var openai = new Mock<IProvider>();
        openai.SetupGet(p => p.Id).Returns("openai");
        openai.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var router = CreateRouter(new Dictionary<string, Mock<IProvider>>
        {
            ["anthropic"] = anthropic,
            ["openai"] = openai,
            ["google"] = new Mock<IProvider>(),
            ["ollama"] = new Mock<IProvider>()
        });

        var result = await router.AutoSelectAsync(EffortLevel.Medium);

        Assert.NotNull(result);
        Assert.Equal("openai", result!.Value.Provider.Id);
        Assert.Equal("gpt-4o", result.Value.Model);
    }

    [Fact]
    public async Task AutoSelectAsync_ReturnsNullIfNoneValid()
    {
        var mocks = new Dictionary<string, Mock<IProvider>>();
        foreach (var id in new[] { "anthropic", "openai", "google", "ollama" })
        {
            var mock = new Mock<IProvider>();
            mock.SetupGet(p => p.Id).Returns(id);
            mock.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            mocks[id] = mock;
        }

        var router = CreateRouter(mocks);
        var result = await router.AutoSelectAsync(EffortLevel.Medium);

        Assert.Null(result);
    }

    [Fact]
    public void GetRegisteredProviderIds_ReturnsAllIds()
    {
        var router = CreateRouter();
        var ids = router.GetRegisteredProviderIds();

        Assert.Contains("anthropic", ids);
        Assert.Contains("openai", ids);
        Assert.Contains("google", ids);
        Assert.Contains("ollama", ids);
    }
}
