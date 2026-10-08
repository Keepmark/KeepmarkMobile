// Two-way bookmark merge (union by identity). Pure: no I/O, no storage,
// inputs never mutated — the same function serves preview (dry-run) and the
// confirmed connect/switch flows, so preview counts always match the merge.
// Port of KeepmarkApp src/shared/merge.ts.
//
// Rules:
// - Remote is the shared truth: its nodes, ids and metadata are kept as-is.
// - Local bookmarks whose normalized URL is absent remotely are adopted with
//   fresh ids (maxRemote+1…) into the matching folder (matched by full title
//   path) or a newly created one.
// - Same URL, different title → remote title wins (counted as a conflict).
// - Separators are unioned from both sides (they carry no identity; guessing
//   matches would delete user data — the merge never deletes anything).
// - Deletes are NOT merged (no tombstones in the protocol): items deleted on
//   one side while the other was offline reappear. The preview says so.
using Keepmark.Core.Models;

namespace Keepmark.Core.Sync;

/// <summary>Counts shown in the pre-merge preview (plain data, no tree).</summary>
public sealed record MergeStats(
    /// <summary>Local bookmarks absent remotely — will upload.</summary>
    int LocalOnly,
    /// <summary>Remote bookmarks absent locally — will download.</summary>
    int RemoteOnly,
    /// <summary>Same URL both sides, different title — remote title kept.</summary>
    int TitleConflicts,
    /// <summary>Local folders with no remote path match — created.</summary>
    int FoldersCreated,
    /// <summary>Local separators adopted (unioned, never matched).</summary>
    int SeparatorsAdopted,
    /// <summary>Bookmarks seen locally / remotely (preview denominators).</summary>
    int LocalCount,
    int RemoteCount,
    /// <summary>Remote lastUpdated timestamp (for display in conflict UI).</summary>
    string? RemoteLastUpdated = null);

public sealed record MergeResult(List<BookmarkNode> Tree, MergeStats Stats);

public static class BookmarkMerge
{
    private const char PathSep = '\n';

    public static MergeResult Merge(List<BookmarkNode> local, List<BookmarkNode> remote)
    {
        var merged = remote.Select(Clone).ToList();
        int nextId = MaxId(merged) + 1;
        int FreshId() => nextId++;

        // Remote indexes over the merged clone (nodes appended below are found).
        var byUrl = new Dictionary<string, BookmarkNode>(StringComparer.Ordinal);
        var foldersByPath = new Dictionary<string, BookmarkNode>(StringComparer.Ordinal);
        // Original remote URL set: remote-only is computed against the remote
        // as received, not against the merged tree (which also holds adopted nodes).
        var remoteUrls = new HashSet<string>(StringComparer.Ordinal);
        IndexRemote(merged, [], byUrl, foldersByPath, remoteUrls);

        int localOnly = 0, titleConflicts = 0, foldersCreated = 0, separatorsAdopted = 0, localCount = 0;
        var localUrls = new HashSet<string>(StringComparer.Ordinal);

        BookmarkNode AdoptSubtree(BookmarkNode node)
        {
            // Fresh ids throughout: adopted subtrees come from the local id
            // namespace and must never collide with remote ids. Counts every
            // adopted bookmark/separator/folder at all levels.
            var adopted = new BookmarkNode
            {
                Id = FreshId(),
                Title = node.Title,
                Url = node.Url,
                Description = node.Description,
                Tags = node.Tags is null ? null : new List<string>(node.Tags),
            };
            if (node.Children is not null)
            {
                adopted.Children = [];
                foreach (var child in node.Children)
                {
                    var sub = AdoptSubtree(child);
                    adopted.Children.Add(sub);
                    if (sub.IsSeparator) separatorsAdopted++;
                    else if (sub.Url is not null) localOnly++;
                    else foldersCreated++;
                }
            }
            else if (node.Url is null)
            {
                adopted.Children = [];
            }

            return adopted;
        }

        void MergeChildren(List<BookmarkNode>? locals, BookmarkNode target, List<string> trail)
        {
            target.Children ??= [];
            foreach (var node in locals ?? [])
            {
                if (node.IsSeparator)
                {
                    var sep = Clone(node);
                    sep.Id = FreshId();
                    target.Children.Add(sep);
                    separatorsAdopted++;
                    continue;
                }

                if (node.Url is not null)
                {
                    // Bookmark: match by normalized URL against the remote side.
                    localCount++;
                    localUrls.Add(NormalizeUrl(node.Url));
                    if (byUrl.TryGetValue(NormalizeUrl(node.Url), out var known))
                    {
                        if (!string.Equals(known.Title, node.Title, StringComparison.Ordinal))
                        {
                            titleConflicts++;
                        }

                        // Remote node kept as-is (ids + metadata untouched).
                    }
                    else
                    {
                        target.Children.Add(new BookmarkNode
                        {
                            Id = FreshId(),
                            Title = node.Title,
                            Url = node.Url,
                            Description = node.Description,
                            Tags = node.Tags is null ? null : new List<string>(node.Tags),
                        });
                        localOnly++;
                    }

                    if (node.Children is { Count: > 0 })
                    {
                        // Corrupt shape (bookmark carrying children): keep the
                        // bookmarks, flatten into the same folder instead of
                        // dropping them.
                        MergeChildren(node.Children, target, trail);
                    }

                    continue;
                }

                // Folder (or url-less node): match by full title path.
                var childTrail = new List<string>(trail) { node.Title ?? string.Empty };
                if (foldersByPath.TryGetValue(PathKey(childTrail), out var match) && match.Children is not null)
                {
                    MergeChildren(node.Children, match, childTrail);
                }
                else
                {
                    var created = new BookmarkNode
                    {
                        Id = FreshId(),
                        Title = node.Title,
                        Description = node.Description,
                        Tags = node.Tags is null ? null : new List<string>(node.Tags),
                        Children = [],
                    };
                    target.Children.Add(created);
                    foldersByPath[PathKey(childTrail)] = created;
                    foldersCreated++;
                    MergeChildren(node.Children, created, childTrail);
                }
            }
        }

        foreach (var root in local)
        {
            var key = PathKey([root.Title ?? string.Empty]);
            if (foldersByPath.TryGetValue(key, out var target))
            {
                MergeChildren(root.Children, target, [root.Title ?? string.Empty]);
                continue;
            }

            // Whole container missing remotely: adopt with fresh ids
            // throughout so no id ever collides with the remote side.
            // Adopted containers themselves are structure, not content.
            var adopted = AdoptSubtree(root);
            merged.Add(adopted);
            foldersByPath[key] = adopted;
            // Register adopted folders for deeper path matches — without
            // touching the remote URL set (adopted nodes are local content,
            // and remote-only must stay computed against the remote as received).
            RegisterFolders(adopted.Children ?? [], [root.Title ?? string.Empty], foldersByPath);
        }

        // Remote-only: unique remote URLs absent locally (informational — the
        // merged tree holds everything by construction).
        var stats = new MergeStats(
            localOnly,
            remoteUrls.Count(u => !localUrls.Contains(u)),
            titleConflicts,
            foldersCreated,
            separatorsAdopted,
            localCount,
            remoteUrls.Count);
        return new MergeResult(merged, stats);
    }

    private static void IndexRemote(
        List<BookmarkNode> nodes, List<string> trail,
        Dictionary<string, BookmarkNode> byUrl,
        Dictionary<string, BookmarkNode> foldersByPath,
        HashSet<string> remoteUrls)
    {
        foreach (var node in nodes)
        {
            if (node.Url is not null && !node.IsSeparator)
            {
                var key = NormalizeUrl(node.Url);
                remoteUrls.Add(key);
                byUrl.TryAdd(key, node);
            }

            if (node.IsFolder || trail.Count == 0)
            {
                // Containers (trail empty) and folders are addressable by path.
                var key = PathKey([.. trail, node.Title ?? string.Empty]);
                foldersByPath.TryAdd(key, node);
            }

            if (node.Children is not null)
            {
                IndexRemote(node.Children, [.. trail, node.Title ?? string.Empty], byUrl, foldersByPath, remoteUrls);
            }
        }
    }

    private static void RegisterFolders(
        List<BookmarkNode> nodes, List<string> trail,
        Dictionary<string, BookmarkNode> foldersByPath)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                foldersByPath.TryAdd(PathKey([.. trail, node.Title ?? string.Empty]), node);
            }

            if (node.Children is not null)
            {
                RegisterFolders(node.Children, [.. trail, node.Title ?? string.Empty], foldersByPath);
            }
        }
    }

    private static string PathKey(IEnumerable<string> trail) =>
        string.Join(PathSep, trail);

    private static int MaxId(List<BookmarkNode> tree)
    {
        int max = 0;
        var stack = new Stack<BookmarkNode>(tree);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Id is { } id && id > max)
            {
                max = id;
            }

            if (node.Children is not null)
            {
                foreach (var child in node.Children)
                {
                    stack.Push(child);
                }
            }
        }

        return max;
    }

    private static BookmarkNode Clone(BookmarkNode node) => new()
    {
        Id = node.Id,
        Title = node.Title,
        Url = node.Url,
        Description = node.Description,
        Tags = node.Tags is null ? null : new List<string>(node.Tags),
        Children = node.Children is null ? null : node.Children.Select(Clone).ToList(),
    };

    /// <summary>
    /// URL identity for merge + duplicates: lowercase host, default ports and
    /// fragments dropped, tracking params (utm_*, fbclid, gclid, msclkid,
    /// mc_cid/mc_eid, igshid) removed. Path + query stay case-sensitive.
    /// Unparsable input falls back to trim + lowercase, fragment and trailing
    /// slash dropped. Port of KeepmarkApp normalizeUrl.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var raw = url.Trim();
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var builder = new UriBuilder(uri)
            {
                Host = uri.Host.ToLowerInvariant(),
                Fragment = string.Empty,
            };
            if ((uri.Scheme == Uri.UriSchemeHttp && uri.Port == 80) ||
                (uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443))
            {
                builder.Port = -1;
            }

            var query = uri.Query;
            if (query.Length > 1)
            {
                var kept = new List<string>();
                foreach (var pair in query.Substring(1).Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    var key = eq < 0 ? pair : pair.Substring(0, eq);
                    if (!IsTrackingParam(key))
                    {
                        kept.Add(pair);
                    }
                }

                builder.Query = string.Join("&", kept);
            }

            var path = builder.Path;
            if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            {
                builder.Path = path.Substring(0, path.Length - 1);
            }

            return builder.Uri.ToString();
        }

        // Unparsable input: legacy trim+lowercase, trailing slash and
        // fragment dropped so `x#frag` still groups with `x`.
        var lower = raw.ToLowerInvariant();
        var hash = lower.IndexOf('#');
        if (hash >= 0)
        {
            lower = lower.Substring(0, hash);
        }

        return lower.EndsWith("/", StringComparison.Ordinal) && lower.Length > 1
            ? lower.Substring(0, lower.Length - 1)
            : lower;
    }

    private static bool IsTrackingParam(string key) =>
        key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "fbclid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "gclid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "msclkid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "igshid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "mc_cid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "mc_eid", StringComparison.OrdinalIgnoreCase);
}
