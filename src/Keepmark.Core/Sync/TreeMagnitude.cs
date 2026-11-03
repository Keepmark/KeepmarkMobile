using Keepmark.Core.Models;

namespace Keepmark.Core.Sync;

/// <summary>
/// Magnitude of a replace: how many nodes exist on one side only. Used to spot
/// a massive, unexpected change (a wipe, a bad import, another client gone
/// wrong) so it gets a safety snapshot even though a normal sync does not.
/// Port of KeepmarkApp changedNodeCount.
/// </summary>
public static class TreeMagnitude
{
    /// <summary>Bookmarks are matched by normalized URL, folders by their path
    /// below the three roots. The roots themselves are skipped: their titles
    /// are browser-localized, so they would look different on every client.</summary>
    public static int ChangedNodeCount(List<BookmarkNode> a, List<BookmarkNode> b) =>
        Count(Urls(a), Urls(b)) + Count(Folders(a), Folders(b));

    private static int Count(HashSet<string> left, HashSet<string> right)
    {
        var n = 0;
        foreach (var value in left)
        {
            if (!right.Contains(value)) n++;
        }

        foreach (var value in right)
        {
            if (!left.Contains(value)) n++;
        }

        return n;
    }

    private static HashSet<string> Urls(List<BookmarkNode> tree)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        Walk(tree, node =>
        {
            if (node.Children is null && !string.IsNullOrEmpty(node.Url) && node.Url != XbsConstants.SeparatorUrl)
            {
                set.Add(BookmarkMerge.NormalizeUrl(node.Url));
            }
        });
        return set;
    }

    private static HashSet<string> Folders(List<BookmarkNode> tree)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Visit(List<BookmarkNode> nodes, List<string> path, int depth)
        {
            foreach (var node in nodes)
            {
                if (node.Children is null) continue;
                var next = new List<string>(path) { node.Title ?? string.Empty };
                // Depth 0 = the three roots: their titles are localized.
                if (depth > 0) set.Add(string.Join(" / ", next));
                Visit(node.Children, next, depth + 1);
            }
        }

        Visit(tree, [], 0);
        return set;
    }

    private static void Walk(List<BookmarkNode> nodes, Action<BookmarkNode> visit)
    {
        foreach (var node in nodes)
        {
            visit(node);
            if (node.Children is not null) Walk(node.Children, visit);
        }
    }
}
