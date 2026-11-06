using System.Text.Json;

using Keepmark.Core.I18n;

namespace Keepmark.Core.Safety;

// Operation labels for safety backups: WHAT the user was doing when the
// overwritten side was snapshotted. Stored in filenames (slugged) and the
// manifest, surfaced verbatim in the Backup page list.
public static class SafetyOperations
{
    public const string TakeRemote = "take-remote";
    public const string UploadLocal = "upload-local";
    public const string CreateEmpty = "create-empty";
    public const string ImportBackup = "import-backup";
    public const string ChangePassword = "change-password";
    // Legacy labels: the retired Move wizard and older sync switches. A
    // switch no longer snapshots the side it leaves (that tree is the sync's
    // server copy) and writes upload-local when it replaces the target's
    // server copy, so switch-take-remote, switch-upload and switch-merge are
    // only read from old files.
    public const string SwitchTakeRemote = "switch-take-remote";
    public const string SwitchUpload = "switch-upload";
    public const string ConnectMerge = "connect-merge";
    public const string SwitchMerge = "switch-merge";
    public const string DuplicatesCleanup = "duplicates-cleanup";
    public const string PreRestore = "pre-restore";
    // Conflict resolution operations
    public const string ConflictPull = "conflict-pull";
    public const string ConflictPush = "conflict-push";
    public const string ConflictMerge = "conflict-merge";
    public const string SelectionDelete = "selection-delete";
    // A sync whose magnitude exceeded XbsConstants.MaxSilentSyncChanges: the
    // side being replaced was snapshotted first (a normal sync takes none).
    public const string SyncPull = "sync-pull";
    public const string SyncPush = "sync-push";
    // Retired: moves and their undo are reversible and no longer snapshot.
    // Kept so the backups written by older versions keep their label.
    public const string SelectionMove = "selection-move";
    public const string SelectionUndo = "selection-move-undo";
    public const string Legacy = "legacy";

    public static string Describe(string operation) => operation switch
    {
        SelectionDelete => Loc.Get(Str.SafetyOpSelectionDelete),
        SelectionMove => Loc.Get(Str.SafetyOpSelectionMove),
        SelectionUndo => Loc.Get(Str.SafetyOpSelectionUndo),
        TakeRemote => Loc.Get(Str.SafetyOpTakeRemote),
        UploadLocal => Loc.Get(Str.SafetyOpUploadLocal),
        CreateEmpty => Loc.Get(Str.SafetyOpCreateEmpty),
        ImportBackup => Loc.Get(Str.SafetyOpImportBackup),
        ChangePassword => Loc.Get(Str.SafetyOpChangePassword),
        SwitchTakeRemote => Loc.Get(Str.SafetyOpSwitchTakeRemote),
        SwitchUpload => Loc.Get(Str.SafetyOpSwitchUpload),
        ConnectMerge => Loc.Get(Str.SafetyOpConnectMerge),
        SwitchMerge => Loc.Get(Str.SafetyOpSwitchMerge),
        DuplicatesCleanup => Loc.Get(Str.SafetyOpDuplicatesCleanup),
        PreRestore => Loc.Get(Str.SafetyOpPreRestore),
        ConflictPull => Loc.Get(Str.SafetyOpConflictPull),
        ConflictPush => Loc.Get(Str.SafetyOpConflictPush),
        ConflictMerge => Loc.Get(Str.SafetyOpConflictMerge),
        SyncPull => Loc.Get(Str.SafetyOpSyncPull),
        SyncPush => Loc.Get(Str.SafetyOpSyncPush),
        _ => Loc.Get(Str.SafetyOpLegacy),
    };
}

/// <summary>One safety-backup file: the overwritten side, snapshotted with
/// WHEN (UTC) and WHY (operation). Plaintext only for legacy / pre-link
/// writes; otherwise encrypted under the safety Master Key.</summary>
public sealed record SafetyBackupEntry(
    string FileName, DateTime CreatedAtUtc, string Operation, string Side,
    int Items, long SizeBytes, string? SyncId, string? Enc = null,
    string? SyncName = null, int? Added = null, int? Removed = null);

/// <summary>What the snapshotted operation changed, when the writer knew the
/// trees before and after (large syncs, deletes). Display only.</summary>
public sealed record SafetyChanges(int Added, int Removed);

/// <summary>Why a safety backup cannot be restored with the current link.</summary>
public enum SafetyLock { None, OtherSync, NeedsLink }

public static class SafetyBackupFiles
{
    public const string IndexFileName = "safety-index.json";
    public const string FilePrefix = "safety-";
    /// <summary>Default retention; the user picks another from KeepChoices in Settings.</summary>
    public const int KeepCount = 10;

    /// <summary>Retention values offered in Settings (same list as the browser extension).</summary>
    public static readonly int[] KeepChoices = [3, 5, 10, 20, 30, 50];

    /// <summary>A stored retention, or the default when it is not one of the choices.</summary>
    public static int NormalizeKeep(int keep) => Array.IndexOf(KeepChoices, keep) >= 0 ? keep : KeepCount;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Human- + file-manager-readable name. Unique per second per
    /// operation; callers disambiguate on collision.</summary>
    public static string BuildFileName(DateTime utc, string operation) =>
        $"{FilePrefix}{utc:yyyyMMdd-HHmmss}-{Slug(operation)}.json";

    public static string Slug(string operation)
    {
        var chars = operation.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrEmpty(slug) ? "backup" : slug;
    }

    public static string Describe(SafetyBackupEntry e) =>
        $"{e.CreatedAtUtc.ToLocalTime():g} · {SafetyOperations.Describe(e.Operation)} · " +
        $"{Loc.Get(e.Side == "local" ? Str.SafetySideLocal : Str.SafetySideServer)} · {Loc.Plural(Str.SafetyItems, e.Items)} · " +
        Loc.Get(e.Enc == "master" ? Str.SafetyEncrypted : Str.SafetyNotEncrypted);

    /// <summary>Truncated sync ID as stored in the manifest (bearer token:
    /// never the full one).</summary>
    public static string? ShortSyncId(string? id) =>
        id is null ? null : id.Length <= 8 ? id : $"{id[..4]}…{id[^2..]}";

    /// <summary>Whether this link can restore the entry. Unlinked, an
    /// encrypted or server-side entry needs a link first; linked, an entry
    /// recorded under another sync is refused: restoring it would push that
    /// sync's bookmarks into this one (the master key opens every encrypted
    /// file, so decryption alone cannot tell). Entries with no recorded sync
    /// (pre-link, plaintext local) restore anywhere.</summary>
    public static SafetyLock Lock(SafetyBackupEntry entry, string? activeSyncId)
    {
        if (activeSyncId is null)
        {
            return entry.Side == "remote" || entry.Enc == "master" ? SafetyLock.NeedsLink : SafetyLock.None;
        }

        return entry.SyncId is not null && entry.SyncId != ShortSyncId(activeSyncId)
            ? SafetyLock.OtherSync
            : SafetyLock.None;
    }

    /// <summary>The saved sync a locked entry belongs to, when exactly one
    /// inactive row matches its truncated ID: the UI offers to activate it.</summary>
    public static Keepmark.Core.Sync.SavedSync? Owner(
        SafetyBackupEntry entry, IEnumerable<Keepmark.Core.Sync.SavedSync> saved, string? activeSyncId)
    {
        if (entry.SyncId is null) return null;
        var matches = saved.Where(r => ShortSyncId(r.SyncId) == entry.SyncId).Take(2).ToList();
        return matches.Count == 1 && matches[0].SyncId != activeSyncId ? matches[0] : null;
    }

    /// <summary>Current name of each entry's sync: a saved sync whose
    /// truncated ID matches exactly one row lends its (possibly renamed) name;
    /// otherwise the name recorded at write time stays.</summary>
    public static List<SafetyBackupEntry> WithSyncNames(
        IEnumerable<SafetyBackupEntry> entries, IEnumerable<Keepmark.Core.Sync.SavedSync> saved)
    {
        var rows = saved.ToList();
        return entries.Select(e =>
        {
            if (e.SyncId is null) return e;
            var matches = rows.Where(r => ShortSyncId(r.SyncId) == e.SyncId).Take(2).ToList();
            return matches.Count == 1 ? e with { SyncName = Keepmark.Core.Sync.SavedSyncs.Name(matches[0]) } : e;
        }).ToList();
    }

    /// <summary>Day heading for the list: Today / Yesterday / a local date.</summary>
    public static string DayLabel(DateTime createdUtc, DateTime nowLocal)
    {
        var day = createdUtc.ToLocalTime().Date;
        var diff = (nowLocal.Date - day).Days;
        return diff switch
        {
            0 => Loc.Get(Str.SafetyToday),
            1 => Loc.Get(Str.SafetyYesterday),
            _ => day.ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture),
        };
    }

    /// <summary>"side · N items · size" line under the entry title.</summary>
    public static string Meta(SafetyBackupEntry e, string size) =>
        $"{Loc.Get(e.Side == "local" ? Str.SafetySideLocal : Str.SafetySideServer)} · {Loc.Plural(Str.SafetyItems, e.Items)} · {size}";

    /// <summary>Filenames to delete: everything beyond the newest <paramref name="keep"/>.</summary>
    public static List<string> Prune(IEnumerable<SafetyBackupEntry> entries, int keep = KeepCount) =>
        entries.OrderByDescending(e => e.CreatedAtUtc)
            .Skip(NormalizeKeep(keep))
            .Select(e => e.FileName)
            .ToList();

    /// <summary>Reads pre-manifest files (safety-{side}-{ts}.json) so old
    /// backups stay listed and restorable. Null when not ours.</summary>
    public static SafetyBackupEntry? ParseLegacy(string fileName, long sizeBytes)
    {
        if (!fileName.StartsWith(FilePrefix, StringComparison.Ordinal) || !fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            return null;
        }

        var inner = fileName.Substring(FilePrefix.Length, fileName.Length - FilePrefix.Length - ".json".Length);
        var dash = inner.IndexOf('-');
        if (dash <= 0)
        {
            return null;
        }

        var side = inner.Substring(0, dash);
        if (side != "local" && side != "remote")
        {
            return null;
        }

        if (!DateTime.TryParseExact(inner.Substring(dash + 1), "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var ts))
        {
            return null;
        }

        return new SafetyBackupEntry(fileName, DateTime.SpecifyKind(ts, DateTimeKind.Utc),
            SafetyOperations.Legacy, side, 0, sizeBytes, null);
    }

    public static string SerializeIndex(IEnumerable<SafetyBackupEntry> entries) =>
        JsonSerializer.Serialize(entries.ToList(), Json);

    public static List<SafetyBackupEntry> ParseIndex(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SafetyBackupEntry>>(raw, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
