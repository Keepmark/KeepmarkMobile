namespace Keepmark.Core.Sync;

/// <summary>One foreground-only poll worker. Inactivation cancels immediately,
/// then drains the current tick before any replacement worker can start.</summary>
public sealed class ForegroundPoller : IAsyncDisposable
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialDelay;
    private readonly Func<CancellationToken, Task> _tick;
    private readonly Action<Exception> _onError;
    private readonly object _state = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private bool _active;
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public ForegroundPoller(TimeSpan interval, Func<CancellationToken, Task> tick,
        Action<Exception> onError, TimeSpan? initialDelay = null)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        var delay = initialDelay ?? TimeSpan.FromMilliseconds(500);
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialDelay));
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(onError);
        _interval = interval;
        _initialDelay = delay;
        _tick = tick;
        _onError = onError;
    }

    public Task SetActiveAsync(bool active)
    {
        lock (_state)
        {
            if (_disposed) return Task.CompletedTask;
            _active = active;
            if (!active) _cts?.Cancel();
        }
        return ReconcileAsync();
    }

    private async Task ReconcileAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? stopping = null;
            lock (_state)
            {
                if (_worker is not null && (!_active || _disposed || _cts!.IsCancellationRequested))
                {
                    _cts!.Cancel();
                    stopping = _worker;
                }
            }
            if (stopping is not null)
            {
                await stopping.ConfigureAwait(false);
                lock (_state)
                {
                    _worker = null;
                    _cts!.Dispose();
                    _cts = null;
                }
            }
            lock (_state)
            {
                if (_active && !_disposed && _worker is null)
                {
                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;
                    _worker = Task.Run(() => PollAsync(token));
                }
            }
        }
        finally { _transition.Release(); }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_initialDelay, ct).ConfigureAwait(false);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { await _tick(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // A diagnostic sink must not terminate the poll worker.
                    try { _onError(ex); } catch { }
                }
                // Fixed delay prevents catch-up storms after a slow/offline tick.
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_state)
        {
            _disposed = true;
            _active = false;
            _cts?.Cancel();
        }
        await ReconcileAsync().ConfigureAwait(false);
    }
}
