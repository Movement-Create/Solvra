using System.Text.Json.Nodes;
using Solvra.Models;
using Solvra.Providers;
using Xunit;

namespace Solvra.Tests.Providers;

public class VisionTests
{
    [Theory]
    [InlineData("chatgpt", "gpt-5.6-sol", true)]
    [InlineData("google", "gemini-2.5-flash", true)]
    [InlineData("ollama", "llama3.1", false)]
    [InlineData("ollama", "llama3.2-vision", true)]
    [InlineData("moonshot", "kimi-k2", false)]
    public void CapabilitiesAreConservative(string provider, string model, bool expected)
        => Assert.Equal(expected, ModelCapabilities.SupportsVision(provider, model));

    [Fact]
    public void GoogleRequestUsesNativeInlineData()
    {
        var provider = new GoogleProvider(apiKey: "test");
        var request = provider.BuildRequest(new CompletionOptions
        {
            Model = "gemini-2.5-flash",
            Messages = [new Message
            {
                Role = MessageRole.User,
                Content =
                [
                    new TextContent { Text = "inspect" },
                    new ImageContent { Source = new ImageSource { SourceType = "base64", MediaType = "image/png", Data = "aGVsbG8=" } }
                ]
            }]
        });

        var inline = request["contents"]![0]!["parts"]![1]!["inlineData"]!.AsObject();
        Assert.Equal("image/png", inline["mimeType"]!.GetValue<string>());
        Assert.Equal("aGVsbG8=", inline["data"]!.GetValue<string>());
    }

    [Fact]
    public void OllamaRequestUsesNativeImagesArray()
    {
        var provider = new OllamaProvider();
        var request = provider.BuildRequest(new CompletionOptions
        {
            Model = "llava",
            Messages = [new Message
            {
                Role = MessageRole.User,
                Content =
                [
                    new TextContent { Text = "inspect" },
                    new ImageContent { Source = new ImageSource { SourceType = "base64", MediaType = "image/png", Data = "aGVsbG8=" } }
                ]
            }]
        }, stream: false);

        Assert.Equal("aGVsbG8=", request["messages"]![0]!["images"]![0]!.GetValue<string>());
    }
}
