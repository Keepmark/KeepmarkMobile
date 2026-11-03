using System.Security.Cryptography;
using System.Text;

using Keepmark.Core.I18n;
using Keepmark.Core.Models;
using Keepmark.Core.Sync;

namespace Keepmark.Core.Safety;

/// <summary>Encrypted local copy of the bookmarks of a sync that no longer
/// exists on its service, kept when the device is unlinked so the last copy of
/// that tree is never lost. Content-addressed by the tree hash (deduplicated);
/// never pruned automatically.</summary>
public sealed record RemovedSyncCopyFile(
    int Format, DateTime CreatedAtUtc, string ServiceUrl, int Items, List<BookmarkNode> Tree);

/// <summary>List entry for one removed-sync copy (no tree loaded).</summary>
public sealed record RemovedSyncCopyInfo(
    string FileName, DateTime CreatedAtUtc, int Items, string ServiceUrl);

public static class RemovedSyncCopies
{
    public const string DirName = "removed-syncs";
    public const string FilePrefix = "removed-";
    public const int CurrentFormat = 1;

    /// <summary>SHA-256 of the serialized tree: the file name and the dedupe key.</summary>
    public static string Hash(string treeJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(treeJson))).ToLowerInvariant();

    public static string FileName(string hash) => $"{FilePrefix}{hash}.json";

    public static string Describe(RemovedSyncCopyInfo info) =>
        $"{info.CreatedAtUtc.ToLocalTime():g} · {SavedSyncs.ServiceHost(info.ServiceUrl)} · " +
        Loc.Plural(Str.SafetyItems, info.Items);
}
