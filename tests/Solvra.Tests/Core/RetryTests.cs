using System.Net;
using Xunit;
using Solvra.Core;

namespace Solvra.Tests.Core;

public class RetryTests
{
    [Fact]
    public async Task WithRetryAsync_SucceedsOnFirstAttempt()
    {
        var callCount = 0;
        var result = await Retry.WithRetryAsync(async () =>
        {
            callCount++;
            return 42;
        });

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task WithRetryAsync_RetriesOnRetryableError()
    {
        var callCount = 0;
        var result = await Retry.WithRetryAsync(async () =>
        {
            callCount++;
            if (callCount < 3)
                throw new HttpRequestException("Server error 500", null, HttpStatusCode.InternalServerError);
            return "success";
        }, new RetryOptions { MaxRetries = 3, BaseDelayMs = 1 });

        Assert.Equal("success", result);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task WithRetryAsync_DoesNotRetryNonRetryable()
    {
        var callCount = 0;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await Retry.WithRetryAsync<string>(async () =>
            {
                callCount++;
                throw new HttpRequestException("Not found", null, HttpStatusCode.NotFound);
            }, new RetryOptions { MaxRetries = 3, BaseDelayMs = 1 });
        });

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task WithRetryAsync_ThrowsAfterMaxRetries()
    {
        var callCount = 0;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await Retry.WithRetryAsync<string>(async () =>
            {
                callCount++;
                throw new HttpRequestException("Rate limit exceeded 429", null, (HttpStatusCode)429);
            }, new RetryOptions { MaxRetries = 2, BaseDelayMs = 1 });
        });

        Assert.Equal(3, callCount); // initial + 2 retries
    }

    [Fact]
    public async Task WithRetryAsync_CallsOnRetryCallback()
    {
        var retryAttempts = new List<int>();
        var callCount = 0;

        var result = await Retry.WithRetryAsync(async () =>
        {
            callCount++;
            if (callCount < 3)
                throw new HttpRequestException("overloaded");
            return "done";
        }, new RetryOptions
        {
            MaxRetries = 3,
            BaseDelayMs = 1,
            OnRetry = async (attempt, _) =>
            {
                retryAttempts.Add(attempt);
                return true;
            }
        });

        Assert.Equal("done", result);
        Assert.Equal(2, retryAttempts.Count);
        Assert.Equal(0, retryAttempts[0]);
        Assert.Equal(1, retryAttempts[1]);
    }

    [Fact]
    public async Task WithRetryAsync_AbortsIfOnRetryReturnsFalse()
    {
        var callCount = 0;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await Retry.WithRetryAsync<string>(async () =>
            {
                callCount++;
                throw new HttpRequestException("503 error", null, HttpStatusCode.ServiceUnavailable);
            }, new RetryOptions
            {
                MaxRetries = 5,
                BaseDelayMs = 1,
                OnRetry = async (attempt, _) => false // abort on first retry
            });
        });

        Assert.Equal(1, callCount);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(529, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    public void IsRetryable_StatusCodes(int statusCode, bool expected)
    {
        var ex = new HttpRequestException($"Error {statusCode}", null, (HttpStatusCode)statusCode);
        Assert.Equal(expected, Retry.IsRetryable(ex));
    }

    [Theory]
    [InlineData("server is overloaded", true)]
    [InlineData("rate limit exceeded", true)]
    [InlineData("too many requests", true)]
    [InlineData("bad request format", false)]
    public void IsRetryable_MessagePatterns(string message, bool expected)
    {
        var ex = new HttpRequestException(message);
        Assert.Equal(expected, Retry.IsRetryable(ex));
    }

    [Fact]
    public void ComputeDelay_ExponentialBackoff()
    {
        // With fixed seed, just verify the pattern
        var delay0 = Retry.ComputeDelay(0, 1000, 30000);
        var delay1 = Retry.ComputeDelay(1, 1000, 30000);
        var delay2 = Retry.ComputeDelay(2, 1000, 30000);

        // delay0 should be ~1000-1500ms
        Assert.InRange(delay0, 1000, 1500);
        // delay1 should be ~2000-2500ms
        Assert.InRange(delay1, 2000, 2500);
        // delay2 should be ~4000-4500ms
        Assert.InRange(delay2, 4000, 4500);
    }

    [Fact]
    public void ComputeDelay_RespectsCap()
    {
        var delay = Retry.ComputeDelay(20, 1000, 30000); // 2^20 * 1000 = huge
        Assert.True(delay <= 30000);
    }
}
