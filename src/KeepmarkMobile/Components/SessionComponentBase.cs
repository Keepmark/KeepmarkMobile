using KeepmarkMobile.Services;
using Microsoft.AspNetCore.Components;

namespace KeepmarkMobile.Components;

// A component can disappear while session initialization or a database read awaits.
// Never attach singleton events or create timers after its lifetime has ended.
public abstract class SessionComponentBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    protected bool IsDisposed { get; private set; }
    protected CancellationToken Lifetime => _lifetime.Token;

    protected async Task<bool> WaitForSessionAsync(XbsSession session)
    {
        try
        {
            await session.InitAsync().WaitAsync(Lifetime);
            return !IsDisposed;
        }
        catch (OperationCanceledException) when (IsDisposed) { return false; }
    }

    public virtual void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
