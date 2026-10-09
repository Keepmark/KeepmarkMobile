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
    public const string Legacy = "legacy";

    public static string Describe(string operation) => operation switch
    {
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
        _ => Loc.Get(Str.SafetyOpLegacy),
    };
}

/// <summary>One safety-backup file: the overwritten side, snapshotted with
/// WHEN (UTC) and WHY (operation). Plaintext only for legacy / pre-link
/// writes; otherwise encrypted under the safety Master Key.</summary>
public sealed record SafetyBackupEntry(
    string FileName, DateTime CreatedAtUtc, string Operation, string Side,
    int Items, long SizeBytes, string? SyncId, string? Enc = null);

public static class SafetyBackupFiles
{
    public const string IndexFileName = "safety-index.json";
    public const string FilePrefix = "safety-";
    public const int KeepCount = 5;

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
        $"{(e.Side == "local" ? "local bookmarks" : "server copy")} · {e.Items} items" +
        (e.Enc == "master" ? " · encrypted" : " · not encrypted");

    /// <summary>Filenames to delete: everything beyond the newest KeepCount.</summary>
    public static List<string> Prune(IEnumerable<SafetyBackupEntry> entries) =>
        entries.OrderByDescending(e => e.CreatedAtUtc)
            .Skip(KeepCount)
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
