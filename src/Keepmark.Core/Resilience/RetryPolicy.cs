namespace Keepmark.Core.Resilience;

/// <summary>Retry rules for API calls. Pure and unit-tested.
/// Retried: network failures, timeouts, 429 (honoring Retry-After), 5xx.
/// Never retried: 400/401/404/405/406/409/412/413, user cancellation,
/// and non-idempotent calls (sync creation AND bookmark updates) — the caller decides those.</summary>
public static class RetryPolicy
{
    public const int MaxAttempts = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    public static bool IsTransient(Exception ex)
        => ex is Errors.ServiceOfflineException
        || ex is Errors.ThrottledException
        || ex is HttpRequestException
        || ex is TaskCanceledException;

    /// <param name="completedAttempts">1-based attempts already made.</param>
    public static (bool Retry, TimeSpan Delay) ForAttempt(Exception ex, int completedAttempts, TimeSpan? retryAfter = null)
    {
        if (!IsTransient(ex) || completedAttempts >= MaxAttempts)
        {
            return (false, TimeSpan.Zero);
        }

        if (retryAfter.HasValue)
        {
            var capped = retryAfter.Value < TimeSpan.Zero ? TimeSpan.Zero : retryAfter.Value;
            return (true, capped > MaxRetryAfter ? MaxRetryAfter : capped);
        }

        var backoff = TimeSpan.FromMilliseconds(BaseDelay.TotalMilliseconds * (1 << (completedAttempts - 1)));
        if (backoff > MaxDelay)
        {
            backoff = MaxDelay;
        }

        // Full jitter: random 0..backoff keeps clients from retrying in lockstep.
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * backoff.TotalMilliseconds);
        return (true, jitter);
    }
}
