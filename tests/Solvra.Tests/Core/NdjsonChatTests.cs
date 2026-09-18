using Xunit;
using Solvra.Core;

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
