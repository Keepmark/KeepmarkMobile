using Keepmark.Core.Errors;
using Keepmark.Core.Resilience;

namespace Keepmark.Core.Tests;

public sealed class RetryPolicyTests
{
    [Theory]
    [InlineData(true)]  // network
    [InlineData(false)] // timeout
    public void Transient_NetworkAndTimeout(bool network)
    {
        Exception ex = network
            ? new HttpRequestException("down")
            : new ServiceOfflineException("timeout");
        var (retry, _) = RetryPolicy.ForAttempt(ex, 1);
        Assert.True(retry);
    }

    [Theory]
    [MemberData(nameof(FatalErrors))]
    public void Fatal_NeverRetried(XbsException ex)
    {
        var (retry, _) = RetryPolicy.ForAttempt(ex, 1);
        Assert.False(retry);
    }

    public static TheoryData<XbsException> FatalErrors() => new()
    {
        new SyncNotFoundException("x"),
        new DataOutOfSyncException("x"),
        new InvalidCredentialsException("x"),
        new SyncTooLargeException("x"),
        new UnexpectedResponseException("x"),
    };

    [Fact]
    public void StopsAfterMaxAttempts()
    {
        var (retry, _) = RetryPolicy.ForAttempt(new HttpRequestException("x"), RetryPolicy.MaxAttempts);
        Assert.False(retry);
    }

    [Fact]
    public void BackoffGrows_AndCaps()
    {
        var (_, d1) = RetryPolicy.ForAttempt(new HttpRequestException("x"), 1);
        var (_, d2) = RetryPolicy.ForAttempt(new HttpRequestException("x"), 2);
        Assert.True(d1 <= TimeSpan.FromMilliseconds(500));
        Assert.True(d2 <= TimeSpan.FromMilliseconds(1000));
        for (var i = 1; i <= RetryPolicy.MaxAttempts; i++)
        {
            var (_, d) = RetryPolicy.ForAttempt(new HttpRequestException("x"), i);
            Assert.True(d <= TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void RetryAfter_HonoredAndCapped()
    {
        var (retry, delay) = RetryPolicy.ForAttempt(
            new ThrottledException("slow"), 1, TimeSpan.FromSeconds(42));
        Assert.True(retry);
        Assert.Equal(TimeSpan.FromSeconds(42), delay);

        var (_, capped) = RetryPolicy.ForAttempt(
            new ThrottledException("slow"), 1, TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromMinutes(5), capped);
    }
}
