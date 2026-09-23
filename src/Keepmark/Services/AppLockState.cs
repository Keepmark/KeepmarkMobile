namespace Keepmark.Services;

// Tracks lock state across pause/resume. The UI (MainLayout) renders a full
// overlay whenever Locked is true. Cold start with app lock on => locked.

public sealed class AppLockState
{
    private bool _locked;

    public bool Locked
    {
        get => _locked;
        private set
        {
            if (_locked != value)
            {
                _locked = value;
                Changed?.Invoke();
            }
        }
    }

    public DateTimeOffset LastPausedUtc { get; private set; } = DateTimeOffset.UtcNow;

    public event Action? Changed;

    public void Initialize(XbsSettings settings) => Locked = settings.AppLock != "Off";

    public void OnPaused(XbsSettings settings)
    {
        LastPausedUtc = DateTimeOffset.UtcNow;
        if (settings.AppLock != "Off" && settings.LockTimeoutMinutes <= 0)
        {
            Locked = true;
        }
    }

    public void OnResumed(XbsSettings settings)
    {
        if (settings.AppLock == "Off")
        {
            Locked = false;
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - LastPausedUtc;
        if (elapsed >= TimeSpan.FromMinutes(Math.Max(0, settings.LockTimeoutMinutes)))
        {
            Locked = true;
        }
    }

    public void Unlock() => Locked = false;
}
