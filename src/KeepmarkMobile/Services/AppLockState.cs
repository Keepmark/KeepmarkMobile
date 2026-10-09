using Keepmark.Core.Security;

namespace KeepmarkMobile.Services;

// Tracks lock state across pause/resume. The UI (MainLayout) renders a full
// overlay whenever Locked is true. Cold start with app lock on => locked.
// Timing rules live in Core LockPolicy; time in the background is measured on
// SystemClock.ElapsedRealtime (monotonic, counts deep sleep, immune to the
// user changing the wall clock).
//
// PromptRequested fires when the app is locked AND in the foreground AND no
// prompt is open: MainLayout then opens the system PIN/biometric prompt
// directly, without an Unlock tap. AuthInProgress keeps pause/resume caused
// by the prompt itself (the device-credential screen can be its own activity)
// from relocking or re-prompting in a loop.

public sealed class AppLockState
{
    private bool _locked;
    private long _pausedAtMs = Android.OS.SystemClock.ElapsedRealtime();

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

    public bool Foreground { get; private set; }

    /// <summary>The system prompt is open (BeginAuth/EndAuth).</summary>
    public bool AuthInProgress { get; private set; }

    /// <summary>The user cancelled the auto prompt: don't reopen it until the
    /// app goes to the background and comes back (manual Unlock still works).</summary>
    public bool AutoPromptSuppressed { get; private set; }

    private long _authEndedMs = long.MinValue / 2;

    // A resume this soon after a prompt closed is the prompt's own activity
    // going away (API 29 device-credential screen), not the user coming back.
    private const long PromptEchoMs = 1500;

    public bool ShouldPrompt => Locked && Foreground && !AuthInProgress && !AutoPromptSuppressed;

    public event Action? Changed;

    public event Action? PromptRequested;

    public void Initialize(XbsSettings settings)
    {
        Locked = settings.AppLock != "Off";
        RequestPromptIfNeeded();
    }

    public void OnPaused(XbsSettings settings)
    {
        Foreground = false;
        if (AuthInProgress)
        {
            return;
        }

        _pausedAtMs = Android.OS.SystemClock.ElapsedRealtime();
        AutoPromptSuppressed = false;
        if (settings.AppLock != "Off" && LockPolicy.LockOnPause(settings.LockTimeoutMinutes))
        {
            Locked = true;
        }
    }

    public void OnResumed(XbsSettings settings)
    {
        Foreground = true;
        if (AuthInProgress)
        {
            return;
        }

        if (settings.AppLock == "Off")
        {
            Locked = false;
            return;
        }

        var now = Android.OS.SystemClock.ElapsedRealtime();
        if (now - _authEndedMs < PromptEchoMs)
        {
            // The prompt's own screen closing: never relock right after a
            // success, never re-prompt in a loop after a cancel.
            if (Locked)
            {
                AutoPromptSuppressed = true;
            }

            return;
        }

        if (LockPolicy.LockOnResume(settings.LockTimeoutMinutes, now - _pausedAtMs))
        {
            Locked = true;
        }

        RequestPromptIfNeeded();
    }

    public void Unlock() => Locked = false;

    public void BeginAuth()
    {
        AuthInProgress = true;
        AutoPromptSuppressed = false;
    }

    /// <summary>A failure while in the foreground is a user cancel: keep the
    /// manual Unlock button and stop auto-prompting. A failure because the
    /// app left the foreground (Home during the prompt) re-prompts on return.</summary>
    public void EndAuth(bool ok)
    {
        AuthInProgress = false;
        _authEndedMs = Android.OS.SystemClock.ElapsedRealtime();
        if (ok)
        {
            Unlock();
        }
        else if (Foreground)
        {
            AutoPromptSuppressed = true;
        }
    }

    private void RequestPromptIfNeeded()
    {
        if (ShouldPrompt)
        {
            PromptRequested?.Invoke();
        }
    }
}
