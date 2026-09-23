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

    public static void RequirePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Enter the backup password.");
        }

        if (password.Length < MinPasswordLength)
        {
            throw new InvalidOperationException(
                $"Use a password of at least {MinPasswordLength} characters.");
        }
    }

    public static void RequirePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Choose a backup file first.");
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
                ?? throw new InvalidOperationException("Backup file is damaged (empty entry).");
            if (++n > MaxNodes)
            {
                throw new InvalidOperationException(
                    $"Backup is too large (over {MaxNodes:N0} items).");
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
