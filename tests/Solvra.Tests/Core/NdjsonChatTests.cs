using Xunit;
using Solvra.Core;
using Solvra.Models;

namespace Solvra.Tests.Core;

public class NdjsonChatTests
{
    [Fact]
    public void ParseCommand_ReadsSendAndPermission()
    {
        var send = NdjsonChat.ParseCommand("""{"t":"send","text":"hi"}""", out var e1);
        Assert.Null(e1);
        Assert.Equal("send", send!.T);
        Assert.Equal("hi", send.Text);

        var perm = NdjsonChat.ParseCommand("""{"t":"permission","id":"call_1","decision":"allow"}""", out var e2);
        Assert.Null(e2);
        Assert.Equal("call_1", perm!.Id);
        Assert.Equal("allow", perm.Decision);
    }

    [Fact]
    public void ParseCommand_ReadsAndValidatesInlineImages()
    {
        var command = NdjsonChat.ParseCommand("""{"t":"send","text":"inspect","images":[{"mime":"image/png","data":"iVBORw0KGgo="}]}""", out var parseError);
        Assert.Null(parseError);
        var message = NdjsonChat.BuildUserMessage(command!, out var validationError);
        Assert.Null(validationError);
        var image = Assert.IsType<ImageContent>(message!.Content[1]);
        Assert.Equal("image/png", image.Source.MediaType);
        Assert.Equal("iVBORw0KGgo=", image.Source.Data);
    }

    [Theory]
    [InlineData("image/gif", "aGVsbG8=", "supported image types")]
    [InlineData("image/png", "not-base64", "valid base64")]
    [InlineData("image/png", "aGVsbG8=", "does not match")]
    public void BuildUserMessage_RejectsInvalidImages(string mime, string data, string expected)
    {
        var command = new NdjsonChat.ProtocolCommand("send", "inspect", null, null, null, null,
            [new NdjsonChat.ProtocolImage(mime, data)]);
        Assert.Null(NdjsonChat.BuildUserMessage(command, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void InlineImageTurnsAreAlwaysToolFree()
    {
        var image = new Message
        {
            Role = MessageRole.User,
            Content = [new TextContent { Text = "inspect" }, new ImageContent { Source = new ImageSource { SourceType = "base64", MediaType = "image/png", Data = "iVBORw0KGgo=" } }]
        };
        Assert.True(NdjsonChat.IsToolFreeTurn(image, noTools: false));
        Assert.True(NdjsonChat.IsToolFreeTurn(Message.FromText(MessageRole.User, "hi"), noTools: true));
        Assert.False(NdjsonChat.IsToolFreeTurn(Message.FromText(MessageRole.User, "hi"), noTools: false));
    }

    [Fact]
    public void ParseCommand_RejectsGarbage()
    {
        Assert.Null(NdjsonChat.ParseCommand("", out var e0));
        Assert.Null(e0);
        Assert.Null(NdjsonChat.ParseCommand("not json", out var e1));
        Assert.Equal("unparseable command", e1);
        Assert.Null(NdjsonChat.ParseCommand("""{"text":"x"}""", out var e2));
        Assert.Equal("missing command type", e2);
    }
}
