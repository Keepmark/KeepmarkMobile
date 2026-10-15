using Keepmark.Core.Sync;

namespace Keepmark.Core.Tests;

public sealed class ForegroundPollerTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InactiveCancelsAndDrainsTickBeforeReturning()
    {
        var started = Signal();
        var cancelled = Signal();
        var release = Signal();
        var finished = false;
        await using var poller = new ForegroundPoller(TimeSpan.FromHours(1), async ct =>
        {
            started.TrySetResult();
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await release.Task;
            finished = true;
        }, _ => Assert.Fail("Unexpected error"), TimeSpan.Zero);
        await poller.SetActiveAsync(true);
        await started.Task.WaitAsync(Timeout);
        var stop = poller.SetActiveAsync(false);
        await cancelled.Task.WaitAsync(Timeout);
        Assert.False(stop.IsCompleted);
        release.TrySetResult();
        await stop.WaitAsync(Timeout);
        Assert.True(finished);
    }

    [Fact]
    public async Task RepeatedActivateAndRapidResumeNeverOverlapWorkers()
    {
        var first = Signal();
        var second = Signal();
        var releaseFirst = Signal();
        var cancellation = Signal();
        int running = 0, maxRunning = 0, calls = 0;
        await using var poller = new ForegroundPoller(TimeSpan.FromHours(1), async ct =>
        {
            int active = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, active);
            int call = Interlocked.Increment(ref calls);
            try
            {
                if (call == 1)
                {
                    using var registration = ct.Register(() => cancellation.TrySetResult());
                    first.TrySetResult();
                    await releaseFirst.Task;
                }
                else
                {
                    second.TrySetResult();
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                }
            }
            finally { Interlocked.Decrement(ref running); }
        }, _ => Assert.Fail("Unexpected error"), TimeSpan.Zero);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => poller.SetActiveAsync(true)));
        await first.Task.WaitAsync(Timeout);
        var stop = poller.SetActiveAsync(false);
        await cancellation.Task.WaitAsync(Timeout);
        var resumes = Enumerable.Range(0, 20).Select(_ => poller.SetActiveAsync(true)).ToArray();
        Assert.Equal(1, calls);
        releaseFirst.TrySetResult();
        await Task.WhenAll(resumes.Append(stop)).WaitAsync(Timeout);
        await second.Task.WaitAsync(Timeout);
        await poller.SetActiveAsync(false).WaitAsync(Timeout);
        Assert.Equal(2, calls);
        Assert.Equal(1, maxRunning);
        Assert.Equal(0, running);
    }

    [Fact]
    public async Task StopDuringInitialDelayDoesNotRunTick()
    {
        int calls = 0;
        await using var poller = new ForegroundPoller(TimeSpan.FromHours(1), _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, _ => { }, TimeSpan.FromHours(1));
        await poller.SetActiveAsync(true);
        await poller.SetActiveAsync(false).WaitAsync(Timeout);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DisposeCancelsDrainsAndPreventsAnyRestart()
    {
        var started = Signal();
        int calls = 0, running = 0;
        var poller = new ForegroundPoller(TimeSpan.FromHours(1), async ct =>
        {
            Interlocked.Increment(ref calls);
            Interlocked.Increment(ref running);
            started.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct); }
            finally { Interlocked.Decrement(ref running); }
        }, _ => { }, TimeSpan.Zero);
        await poller.SetActiveAsync(true);
        await started.Task.WaitAsync(Timeout);
        await Task.WhenAll(poller.DisposeAsync().AsTask(), poller.SetActiveAsync(true), poller.DisposeAsync().AsTask()).WaitAsync(Timeout);
        await poller.SetActiveAsync(true);
        Assert.Equal(1, calls);
        Assert.Equal(0, running);
    }

    [Fact]
    public async Task FaultIsReportedOnceWithoutImmediateRetry()
    {
        var reported = Signal();
        int calls = 0, errors = 0;
        var expected = new InvalidOperationException("tick failed");
        await using var poller = new ForegroundPoller(TimeSpan.FromHours(1), _ =>
        {
            Interlocked.Increment(ref calls);
            throw expected;
        }, ex =>
        {
            Assert.Same(expected, ex);
            Interlocked.Increment(ref errors);
            reported.TrySetResult();
        }, TimeSpan.Zero);
        await poller.SetActiveAsync(true);
        await reported.Task.WaitAsync(Timeout);
        await poller.SetActiveAsync(false).WaitAsync(Timeout);
        Assert.Equal(1, calls);
        Assert.Equal(1, errors);
    }
}
