using System.Text.Json;
using Keepmark.Core.Crypto;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;

namespace Keepmark.Core.Safety;

/// <summary>
/// Storage for copies of removed syncs: one encrypted JSON file per distinct
/// tree, named by the tree hash, in a caller-supplied directory. Kept out of
/// the platform layer so the pipeline (validate → write → verify → read →
/// delete, path guard included) is unit-testable.
/// </summary>
public sealed class RemovedSyncCopyStore(
    string directory,
    ICryptoService crypto,
    Func<Task<string>> deviceKey,
    IXbsLocalizer str,
    IXbsLogger log)
{
    /// <summary>Directory holding the copies (created on first write).</summary>
    public string Directory { get; } = directory;

    /// <summary>
    /// Keeps an encrypted copy of <paramref name="roots"/> and verifies it end
    /// to end before returning. Content-addressed: the same tree is stored
    /// once, whichever sync produced it (the label follows the latest one).
    /// Returns null when there is nothing to keep (empty tree).
    /// </summary>
    public async Task<(string FileName, int Items)?> KeepAsync(
        string serviceUrl, List<BookmarkNode> roots, CancellationToken ct = default)
    {
        var count = BackupGuard.ValidateTree(roots);
        if (count == 0)
        {
            return null;
        }

        var treeJson = JsonSerializer.Serialize(roots);
        var hash = RemovedSyncCopies.Hash(treeJson);
        System.IO.Directory.CreateDirectory(Directory);
        var name = RemovedSyncCopies.FileName(hash);
        var path = Path.Combine(Directory, name);

        if (File.Exists(path))
        {
            var existing = await TryReadAsync(path, ct).ConfigureAwait(false);
            if (existing is not null && HashOf(existing) == hash)
            {
                if (string.Equals(existing.ServiceUrl, serviceUrl, StringComparison.Ordinal))
                {
                    log.Info("safety", "Removed-sync copy already kept (same tree)");
                    return (name, existing.Items);
                }

                // Same tree, another sync: rewrite so the label names the sync
                // that produced it last, and keep a single file per tree.
                log.Info("safety", "Removed-sync copy kept (same tree, relabelled)");
            }
        }

        var envelope = new RemovedSyncCopyFile(
            RemovedSyncCopies.CurrentFormat, DateTime.UtcNow, serviceUrl, count, roots);
        var payload = crypto.Encrypt(
            JsonSerializer.Serialize(envelope), await deviceKey().ConfigureAwait(false));
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, payload, ct).ConfigureAwait(false);
            if (!await MatchesAsync(tmp, hash, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(str.Get(Str.ErrCopyVerify));
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // A crash or a failed verification never leaves half a copy behind.
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        log.Info("safety", $"Kept local copy of removed sync ({count} items)");
        return (name, count);
    }

    /// <summary>Lists the copies (no tree loaded). Damaged or foreign files are
    /// skipped with a warning, never listed and never deleted.</summary>
    public async Task<List<RemovedSyncCopyInfo>> ListAsync(CancellationToken ct = default)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var list = new List<RemovedSyncCopyInfo>();
        foreach (var file in new DirectoryInfo(Directory).GetFiles(RemovedSyncCopies.FilePrefix + "*.json"))
        {
            try
            {
                var copy = await ReadFileAsync(file.FullName, ct).ConfigureAwait(false);
                list.Add(new RemovedSyncCopyInfo(file.Name, copy.CreatedAtUtc, copy.Items, copy.ServiceUrl));
            }
            catch (Exception ex)
            {
                log.Warn("safety", $"Removed-sync copy unreadable ({file.Name}): {ex.GetType().Name}");
            }
        }

        return list.OrderByDescending(c => c.CreatedAtUtc).ToList();
    }

    /// <summary>Loads one copy by file name (guarded: bare file names only).</summary>
    public async Task<RemovedSyncCopyFile> ReadAsync(string fileName, CancellationToken ct = default)
    {
        var path = PathFor(fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(str.Get(Str.ErrBackupPruned));
        }

        return await ReadFileAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes one copy (confirmed in the UI). Never automatic.</summary>
    public void Delete(string fileName)
    {
        var path = PathFor(fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(str.Get(Str.ErrBackupPruned));
        }

        File.Delete(path);
        log.Info("safety", "Removed-sync copy deleted");
    }

    /// <summary>Full path of a copy file. Rejects anything that is not a bare
    /// "removed-*.json" name inside the directory (no traversal, no absolute
    /// paths, no other files).</summary>
    public string PathFor(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
            !fileName.StartsWith(RemovedSyncCopies.FilePrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(str.Get(Str.ErrUnknownBackup));
        }

        var expected = Path.Combine(Directory, fileName);
        if (!string.Equals(Path.GetFullPath(expected), expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(str.Get(Str.ErrUnknownBackup));
        }

        return expected;
    }

    private async Task<RemovedSyncCopyFile> ReadFileAsync(string path, CancellationToken ct)
    {
        var raw = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var json = crypto.Decrypt(raw, await deviceKey().ConfigureAwait(false));
        var copy = JsonSerializer.Deserialize<RemovedSyncCopyFile>(json);
        if (copy is null || copy.Format != RemovedSyncCopies.CurrentFormat || copy.Tree is null)
        {
            throw new InvalidOperationException(str.Get(Str.ErrBackupUnreadable));
        }

        return copy;
    }

    private async Task<RemovedSyncCopyFile?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            return await ReadFileAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<bool> MatchesAsync(string path, string expectedHash, CancellationToken ct)
    {
        var copy = await TryReadAsync(path, ct).ConfigureAwait(false);
        return copy is not null && HashOf(copy) == expectedHash;
    }

    private static string HashOf(RemovedSyncCopyFile copy) =>
        RemovedSyncCopies.Hash(JsonSerializer.Serialize(copy.Tree));
}
