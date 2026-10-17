using Keepmark.Core.Sync;

namespace KeepmarkMobile.Services;

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

    public IReadOnlyList<string> SavedServices
    {
        get
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(Preferences.Get("saved_services", "[]")) ?? []; }
            catch (System.Text.Json.JsonException) { return []; }
        }
    }

    // Called only after an actual successful link/create/move, never a connectivity probe.
    public void RememberService(string url)
    {
        Preferences.Set("saved_services", System.Text.Json.JsonSerializer.Serialize(Core.Workflow.ServiceCatalog.Remember(SavedServices, url)));
        ServiceUrl = Core.Workflow.ServiceCatalog.Normalize(url);
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

    /// <summary>Relock after this many minutes in the background: 0 = at once,
    /// -1 = only on app restart (see Core LockPolicy). Always a preset: old
    /// free-form values snap down to the next stricter one. Default 5.
    /// Applies only when AppLock is configured (anything but Off).</summary>
    public int LockTimeoutMinutes
    {
        get => Core.Security.LockPolicy.Snap(Preferences.Get(KLockTimeoutMin, Core.Security.LockPolicy.DefaultMinutes));
        set => Preferences.Set(KLockTimeoutMin, Core.Security.LockPolicy.Snap(value));
    }

    /// <summary>Color theme: System (default), Light or Dark.</summary>
    public string Theme
    {
        get => Preferences.Get("theme", "System");
        set => Preferences.Set("theme", value);
    }

    /// <summary>UI language: "system" (default, the Android language) or en/it/fr/es/de.</summary>
    public string Language
    {
        get => Preferences.Get("ui_language", "system");
        set => Preferences.Set("ui_language", value);
    }

    public string OpenMode
    {
        get => Preferences.Get(KOpenMode, "CustomTabs");
        set => Preferences.Set(KOpenMode, value);
    }

    /// <summary>Home shows the "Recently added" section. Default on.</summary>
    public bool ShowRecent
    {
        get => Preferences.Get("home_show_recent", true);
        set => Preferences.Set("home_show_recent", value);
    }

    /// <summary>Bookmarks in the Home "Recently added" section: 5, 10 or 20. Default 5.</summary>
    public int RecentCount
    {
        get => Preferences.Get("home_recent_count", 5) is var n && n is 5 or 10 or 20 ? n : 5;
        set => Preferences.Set("home_recent_count", value is 5 or 10 or 20 ? value : 5);
    }

    /// <summary>Last open/closed state of the Home "Recently added" section. Default open.</summary>
    public bool RecentOpen
    {
        get => Preferences.Get("home_recent_open", true);
        set => Preferences.Set("home_recent_open", value);
    }

    /// <summary>List order: Default (server order), Asc, Desc.</summary>
    public string ListSort
    {
        get => Preferences.Get("list_sort", "Default");
        set => Preferences.Set("list_sort", value);
    }
}
