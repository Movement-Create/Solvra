using System.Net;
using Xunit;
using Solvra.Providers;

namespace Solvra.Tests.Providers;

public class HttpErrorsTests
{
    [Fact]
    public void Describe_UsesNestedGatewayMessage()
    {
        var body = """{"type":"error","error":{"type":"GoUsageLimitError","message":"Weekly usage limit reached. Resets in 2 days."}}""";
        var text = HttpErrors.Describe("OpenAI", HttpStatusCode.TooManyRequests, body);
        Assert.Equal("OpenAI API error 429: Weekly usage limit reached. Resets in 2 days.", text);
    }

    [Fact]
    public void Describe_UsesStringErrorAndTopLevelMessage()
    {
        Assert.Equal("X API error 400: bad", HttpErrors.Describe("X", HttpStatusCode.BadRequest, """{"error":"bad"}"""));
        Assert.Equal("X API error 500: boom", HttpErrors.Describe("X", HttpStatusCode.InternalServerError, """{"message":"boom"}"""));
    }

    [Fact]
    public void Describe_FallsBackToRawTextOrStatus()
    {
        Assert.Equal("X API error 502: upstream down", HttpErrors.Describe("X", HttpStatusCode.BadGateway, "  upstream down "));
        Assert.Equal("X API error 503", HttpErrors.Describe("X", HttpStatusCode.ServiceUnavailable, ""));
    }
}
