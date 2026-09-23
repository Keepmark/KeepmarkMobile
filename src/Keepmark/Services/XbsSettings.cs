using Keepmark.Core.Sync;

namespace Keepmark.Services;

// Non-sensitive settings in Preferences; secrets live in SecureStorage via
// SecureCredentialStore. All defaults mirror the agreed plan.

public sealed class XbsSettings
{
    private const string KServiceUrl = "service_url";
    private const string KSyncMode = "sync_mode";
    private const string KVerbosity = "log_verbosity";
    private const string KAppLock = "app_lock";
    private const string KLockTimeoutMin = "lock_timeout_min";
    private const string KOpenMode = "open_mode";

    public string ServiceUrl
    {
        get => Preferences.Get(KServiceUrl, Core.XbsConstants.DefaultServiceUrl);
        set => Preferences.Set(KServiceUrl, value);
    }

    public SyncMode Mode
    {
        get => Enum.TryParse<SyncMode>(Preferences.Get(KSyncMode, SyncMode.Auto.ToString()), out var m) ? m : SyncMode.Auto;
        set => Preferences.Set(KSyncMode, value.ToString());
    }

    public Core.Logging.XbsLogLevel Verbosity
    {
        get => Enum.TryParse<Core.Logging.XbsLogLevel>(Preferences.Get(KVerbosity, Core.Logging.XbsLogLevel.Info.ToString()), out var v)
            ? v : Core.Logging.XbsLogLevel.Info;
        set => Preferences.Set(KVerbosity, value.ToString());
    }

    /// <summary>Off / BiometricOnly / BiometricOrDeviceCredential. Default Off (plan).</summary>
    public string AppLock
    {
        get => Preferences.Get(KAppLock, "Off");
        set => Preferences.Set(KAppLock, value);
    }

    /// <summary>Auto-lock timeout in minutes. 0 = on background. Default 10.
    /// Applies only when AppLock is configured (anything but Off).</summary>
    public int LockTimeoutMinutes
    {
        get => Preferences.Get(KLockTimeoutMin, 10);
        set => Preferences.Set(KLockTimeoutMin, value);
    }

    public string OpenMode
    {
        get => Preferences.Get(KOpenMode, "CustomTabs");
        set => Preferences.Set(KOpenMode, value);
    }

    /// <summary>List order: Default (server order), Asc, Desc.</summary>
    public string ListSort
    {
        get => Preferences.Get("list_sort", "Default");
        set => Preferences.Set("list_sort", value);
    }
}
