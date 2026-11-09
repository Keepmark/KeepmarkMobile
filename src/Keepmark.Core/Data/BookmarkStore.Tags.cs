using Keepmark.Core.Logging;
using Keepmark.Core.Models;

namespace Keepmark.Core.Data;

/// <summary>One tag as the tag view shows it: spellings that differ only by
/// case are one tag, named after the most used spelling.</summary>
public sealed record TagInfo(string Name, int Count);

/// <summary>Every tag in use plus the number of bookmarks that carry none.</summary>
public sealed record TagSummary(List<TagInfo> Tags, int Untagged);

// Tag view: list, filter and rename / merge / delete. Tags live only in the
// Items.Tags column (U+001F-joined); nothing here adds a table or an index.
// Matching is always on the whole tag, case-insensitive (never FTS, which
// would also match title words and prefixes).
public sealed partial class BookmarkStore
{
    /// <summary>Characters a tag cannot hold: the edit sheet splits on the
    /// comma, upstream clients also on the semicolon, the store on U+001F.</summary>
    public static bool IsValidTagName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny([',', ';', TagSep]) < 0;

    private static string[] SplitTags(string? tags) =>
        string.IsNullOrEmpty(tags) ? [] : tags.Split(TagSep, StringSplitOptions.RemoveEmptyEntries);

    private Task<List<ItemRow>> BookmarkRowsAsync() => Db.QueryAsync<ItemRow>(
        "SELECT * FROM Items WHERE IsFolder = 0 AND Url IS NOT NULL AND Url != ? ORDER BY SortOrder",
        XbsConstants.SeparatorUrl);

    /// <summary>Tags in use, sorted by name. A bookmark counts once per tag
    /// even when it repeats it with another case.</summary>
    public async Task<TagSummary> GetTagsAsync()
    {
        var rows = await BookmarkRowsAsync().ConfigureAwait(false);
        // tag (case-insensitive) -> spelling -> bookmarks using that spelling
        var groups = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int untagged = 0;
        foreach (var row in rows)
        {
            var tags = SplitTags(row.Tags);
            if (tags.Length == 0)
            {
                untagged++;
                continue;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                if (!groups.TryGetValue(tag, out var spellings))
                {
                    groups[tag] = spellings = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                spellings[tag] = spellings.GetValueOrDefault(tag) + 1;
                if (seen.Add(tag))
                {
                    counts[tag] = counts.GetValueOrDefault(tag) + 1;
                }
            }
        }

        var list = groups
            .Select(g => new TagInfo(
                g.Value.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).First().Key,
                counts[g.Key]))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        return new TagSummary(list, untagged);
    }

    /// <summary>Bookmarks carrying <paramref name="tag"/> (whole tag, any
    /// case), or the ones with no tag at all when it is null. Display order.</summary>
    public async Task<List<ItemRow>> GetBookmarksByTagAsync(string? tag)
    {
        var rows = await BookmarkRowsAsync().ConfigureAwait(false);
        return tag is null
            ? rows.Where(r => SplitTags(r.Tags).Length == 0).ToList()
            : rows.Where(r => SplitTags(r.Tags).Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Renames <paramref name="tag"/> (every spelling) to
    /// <paramref name="newName"/>, or removes it when that is null, in ONE
    /// transaction. Renaming onto a tag a bookmark already has merges the two
    /// (one entry left, spelled as <paramref name="newName"/>). Other tags and
    /// their order are untouched. Returns the number of changed bookmarks.
    /// One caller = one push.</summary>
    public async Task<int> RenameTagAsync(string tag, string? newName)
    {
        newName = newName?.Trim();
        if (newName is not null && !IsValidTagName(newName))
        {
            throw new ArgumentException("Invalid tag name.", nameof(newName));
        }

        var db = Db;
        int changed = 0;
        await db.RunInTransactionAsync(tran =>
        {
            var rows = tran.Query<ItemRow>("SELECT * FROM Items WHERE Tags IS NOT NULL AND Tags != ''");
            foreach (var row in rows)
            {
                var tags = SplitTags(row.Tags);
                if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = new List<string>(tags.Length);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in tags)
                {
                    bool isOld = string.Equals(t, tag, StringComparison.OrdinalIgnoreCase);
                    if (isOld && newName is null)
                    {
                        continue;
                    }

                    var value = newName is not null &&
                        (isOld || string.Equals(t, newName, StringComparison.OrdinalIgnoreCase)) ? newName : t;
                    if (seen.Add(value))
                    {
                        result.Add(value);
                    }
                }

                var joined = result.Count > 0 ? string.Join(TagSep, result) : null;
                if (joined == row.Tags)
                {
                    continue;
                }

                row.Tags = joined;
                tran.Update(row);
                changed++;
            }

            if (changed > 0)
            {
                Interlocked.Increment(ref _contentVersion);
                tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
            }
        }).ConfigureAwait(false);

        _log.Debug("store", $"Tag {(newName is null ? "removed from" : "renamed on")} {changed} bookmark(s)");
        return changed;
    }
}
