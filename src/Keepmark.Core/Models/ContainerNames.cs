using Keepmark.Core.I18n;

namespace Keepmark.Core.Models;

/// <summary>
/// Display-only names for the top-level xBrowserSync containers. The synced
/// titles ("[xbs] Menu" …) are the wire format every client looks containers
/// up by: they are never rewritten, only shown with a localized name.
/// </summary>
public static class ContainerNames
{
    public const string Menu = "[xbs] Menu";
    public const string Other = "[xbs] Other";
    public const string Toolbar = "[xbs] Toolbar";

    /// <summary>Ensures the three synced containers exist as top-level folders.
    /// A push must never upload a tree without them: a client that omits one
    /// (an old build, a half-imported mirror) would otherwise delete that
    /// container for every other client on the next sync. Returns the nodes it
    /// added, so the caller can persist them into the local mirror too.</summary>
    public static List<BookmarkNode> EnsureContainers(List<BookmarkNode> roots)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        var maxId = 0;

        void Walk(List<BookmarkNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Id is int id && id > maxId) maxId = id;
                if (node.Children is not null) Walk(node.Children);
            }
        }

        foreach (var root in roots)
        {
            present.Add(root.Title ?? string.Empty);
        }

        Walk(roots);
        var added = new List<BookmarkNode>();
        foreach (var name in new[] { Menu, Other, Toolbar })
        {
            if (present.Contains(name)) continue;
            var node = new BookmarkNode { Id = ++maxId, Title = name, Children = [] };
            roots.Add(node);
            added.Add(node);
        }

        return added;
    }

    /// <summary>Localized name for a top-level container (no parent);
    /// any other title, including a nested folder named "[xbs] Menu", is returned unchanged.</summary>
    public static string? Display(string? title, bool isTopLevel)
    {
        if (!isTopLevel)
        {
            return title;
        }

        return title switch
        {
            Menu => Loc.Get(Str.ContainerMenu),
            Other => Loc.Get(Str.ContainerOther),
            Toolbar => Loc.Get(Str.ContainerToolbar),
            _ => title,
        };
    }
}
