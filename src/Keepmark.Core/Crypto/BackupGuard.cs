using Keepmark.Core.I18n;
using Keepmark.Core.Models;

namespace Keepmark.Core.Crypto;

/// <summary>
/// Parameter/file/tree validation for encrypted backup export + import.
/// Session and UI both go through here so the rules are unit-tested once.
/// </summary>
public static class BackupGuard
{
    /// <summary>Minimum backup password length (mirrors the UI hint).</summary>
    public const int MinPasswordLength = 8;

    /// <summary>Largest backup file accepted (bookmarks are KBs; bounds memory use).</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>Largest node tree accepted (bounds the restore transaction).</summary>
    public const int MaxNodes = 200_000;

    /// <summary>Backup files written by this app end with this suffix.</summary>
    public const string FileSuffix = ".xbs.json";

    /// <summary>Longest slug kept from a sync name in a file name.</summary>
    public const int MaxSlugLength = 40;

    /// <summary>
    /// Slug for a file name: lowercase, accents folded, anything that is not a
    /// letter or digit collapsed into one underscore (spaces included), no
    /// leading or trailing underscore. Returns '' when nothing usable is left.
    /// Port of KeepmarkApp backupSlug.
    /// </summary>
    public static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var folded = new string(value.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray())
            .ToLowerInvariant();

        var chars = folded.Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_').ToArray();
        var slug = new string(chars).Trim('_');
        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        return slug.Length <= MaxSlugLength ? slug.TrimEnd('_') : slug[..MaxSlugLength].TrimEnd('_');
    }

    /// <summary>
    /// Export file name: `xbs-backup-&lt;sync&gt;-YYYYMMDD-HHmmss.xbs.json`. The
    /// sync name is a human label (service host fallback), so it is slugged;
    /// without one the file keeps the plain timestamped name.
    /// </summary>
    public static string ExportFileName(string? syncName, DateTime utcNow)
    {
        var slug = Slug(syncName);
        return slug.Length == 0
            ? $"xbs-backup-{utcNow:yyyyMMdd-HHmmss}{FileSuffix}"
            : $"xbs-backup-{slug}-{utcNow:yyyyMMdd-HHmmss}{FileSuffix}";
    }

    public static void RequirePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(Loc.Get(Str.ErrBackupPasswordEnter));
        }

        if (password.Length < MinPasswordLength)
        {
            throw new InvalidOperationException(
                Loc.Get(Str.ErrPasswordLength, MinPasswordLength));
        }
    }

    public static void RequirePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(Loc.Get(Str.ErrChooseBackupFirst));
        }
    }

    public static bool HasSupportedExtension(string fileName) =>
        fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates a decrypted tree (no null nodes, bounded size); returns the node count.</summary>
    public static int ValidateTree(List<BookmarkNode> roots)
    {
        int n = 0;
        var queue = new Queue<BookmarkNode>(roots);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue()
                ?? throw new InvalidOperationException(Loc.Get(Str.ErrBackupEmptyEntry));
            if (++n > MaxNodes)
            {
                throw new InvalidOperationException(
                    Loc.Get(Str.ErrBackupTooManyItems, MaxNodes.ToString("N0")));
            }

            if (cur.Children is not null)
            {
                foreach (var c in cur.Children)
                {
                    queue.Enqueue(c);
                }
            }
        }

        return n;
    }
}
