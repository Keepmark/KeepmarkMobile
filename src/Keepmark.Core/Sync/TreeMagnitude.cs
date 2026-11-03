using Keepmark.Core.Models;

namespace Keepmark.Core.Sync;

/// <summary>
/// Magnitude of a replace: how many nodes changed existence or position. Used
/// to spot a massive, unexpected change (a wipe, a bad import, another client
/// gone wrong) so it gets a safety snapshot even though a normal sync does not.
/// Port of KeepmarkApp changedNodeCount.
/// </summary>
public static class TreeMagnitude
{
    /// <summary>Bookmarks are matched by normalized URL and folders by their
    /// path below the three roots (ids are reassigned by other clients, so they
    /// cannot be used). Counted per parent, so a MOVE is a removal plus an
    /// addition: moving a folder that holds more than half
    /// <see cref="XbsConstants.MaxSilentSyncChanges"/> bookmarks is massive too.
    /// The roots themselves are skipped: their titles are browser-localized,
    /// so they would look different on every client.</summary>
    public static int ChangedNodeCount(List<BookmarkNode> a, List<BookmarkNode> b)
    {
        var left = Buckets(a);
        var right = Buckets(b);
        var keys = new HashSet<string>(left.Keys, StringComparer.Ordinal);
        keys.UnionWith(right.Keys);
        var magnitude = 0;
        foreach (var key in keys)
        {
            left.TryGetValue(key, out var l);
            right.TryGetValue(key, out var r);
            magnitude += Math.Abs(l - r);
        }

        return magnitude;
    }

    private static Dictionary<string, int> Buckets(List<BookmarkNode> tree)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(string parent, string key)
        {
            var composite = parent + "\u0000" + key;
            map[composite] = map.TryGetValue(composite, out var n) ? n + 1 : 1;
        }

        void Visit(List<BookmarkNode> nodes, List<string> path, int depth)
        {
            var parent = string.Join(" / ", path);
            foreach (var node in nodes)
            {
                if (node.Children is not null)
                {
                    var next = new List<string>(path) { node.Title ?? string.Empty };
                    // Depth 0 = the three roots: their titles are localized.
                    if (depth > 0) Add(parent, "f:" + (node.Title ?? string.Empty));
                    Visit(node.Children, next, depth + 1);
                }
                else if (!string.IsNullOrEmpty(node.Url) && node.Url != XbsConstants.SeparatorUrl)
                {
                    Add(parent, "b:" + BookmarkMerge.NormalizeUrl(node.Url));
                }
            }
        }

        Visit(tree, [], 0);
        return map;
    }
}
