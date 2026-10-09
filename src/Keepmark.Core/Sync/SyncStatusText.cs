using Keepmark.Core.I18n;

namespace Keepmark.Core.Sync;

public static class SyncStatusText
{
    /// <summary>The status as a word in the active UI language.</summary>
    public static string Label(SyncStatus status) => status switch
    {
        SyncStatus.Idle => Loc.Get(Str.StatusIdle),
        SyncStatus.Syncing => Loc.Get(Str.StatusSyncing),
        SyncStatus.Offline => Loc.Get(Str.StatusOffline),
        SyncStatus.Conflict => Loc.Get(Str.StatusConflict),
        SyncStatus.Error => Loc.Get(Str.StatusError),
        _ => Loc.Get(Str.StatusNoCredentials),
    };
}
