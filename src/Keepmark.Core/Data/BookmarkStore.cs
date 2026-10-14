using Keepmark.Core.I18n;
using SQLite;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;

namespace Keepmark.Core.Data;

// Persisted rows. The tree structure is ParentRowId/SortOrder; FTS5 mirrors
// text columns for instant search. Tags are unit-separator-joined.

[Table("Meta")]
public sealed class MetaRow
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}

[Table("Blob")]
public sealed class BlobRow
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}

[Table("Items")]
public sealed class ItemRow
{
    [PrimaryKey, AutoIncrement]
    public int RowId { get; set; }
    public int? NodeId { get; set; }
    public int? ParentRowId { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
    public string? Tags { get; set; }
    /// <summary>Synced dateAdded (Unix ms), null when the node carries none.</summary>
    public long? DateAdded { get; set; }
    public bool IsFolder { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Title for the UI: top-level "[xbs] …" containers get a localized
    /// name. Display only — never write it back (Title is the synced value).</summary>
    [Ignore]
    public string? DisplayTitle => ContainerNames.Display(Title, ParentRowId is null);
}

public sealed record SearchHit(
    int RowId, int? NodeId, string? Title, string? Url, string? Description,
    string? Tags, bool IsFolder, string FolderPath, double Rank);

public sealed record ContentStats(
    int Folders, int Bookmarks, int Separators, int DistinctTags,
    int MaxDepth, int DuplicateUrls);

public sealed record DuplicateGroup(string Url, List<ItemRow> Rows);

/// <summary>
/// Encrypted local mirror (SQLCipher) + FTS5 search index.
/// The encryption key (64 hex chars) is supplied by the platform layer
/// (Android KeyStore); Core never stores it.
/// </summary>
public sealed class BookmarkStore : IAsyncDisposable
{
    public const string MetaLastUpdated = "lastUpdated";
    public const string MetaVersion = "syncVersion";
    public const string MetaRevision = "syncRevision";
    public const string MetaHash = "syncHash";
    public const string MetaDirty = "dirty";
    public const string BlobBookmarks = "bookmarks";

    private const char TagSep = '\u001F';

    private readonly string _dbPath;
    private readonly string _keyHex;
    private readonly IXbsLogger _log;
    private SQLiteAsyncConnection? _db;

    public BookmarkStore(string dbPath, string keyHex, IXbsLogger log)
    {
        _dbPath = dbPath;
        _keyHex = keyHex;
        _log = log;
    }

    public async Task InitializeAsync()
    {
        SQLitePCL.Batteries_V2.Init();
        var db = new SQLiteAsyncConnection(_dbPath);
        // NOTE: PRAGMA key returns a row under SQLCipher, so ExecuteScalar (not Execute)
        // must be used — sqlite-net throws "not an error" on row-returning pragmas.
        await db.ExecuteScalarAsync<int>($"PRAGMA key = \"x'{_keyHex}'\"").ConfigureAwait(false);
        // Verify key: unreadable DB throws here on first real access.
        await db.ExecuteScalarAsync<int>("SELECT count(*) FROM sqlite_master").ConfigureAwait(false);

        await db.CreateTablesAsync<MetaRow, BlobRow, ItemRow>().ConfigureAwait(false);
        await db.ExecuteAsync(
            "CREATE VIRTUAL TABLE IF NOT EXISTS ItemsFts USING fts5(Title, Url, Description, Tags, content='Items', content_rowid='RowId')")
            .ConfigureAwait(false);
        await db.ExecuteAsync(
            """
            CREATE TRIGGER IF NOT EXISTS Items_ai AFTER INSERT ON Items BEGIN
              INSERT INTO ItemsFts(rowid, Title, Url, Description, Tags)
              VALUES (new.RowId, new.Title, new.Url, new.Description, new.Tags);
            END
            """).ConfigureAwait(false);
        await db.ExecuteAsync(
            """
            CREATE TRIGGER IF NOT EXISTS Items_ad AFTER DELETE ON Items BEGIN
              INSERT INTO ItemsFts(ItemsFts, rowid, Title, Url, Description, Tags)
              VALUES ('delete', old.RowId, old.Title, old.Url, old.Description, old.Tags);
            END
            """).ConfigureAwait(false);
        await db.ExecuteAsync(
            """
            CREATE TRIGGER IF NOT EXISTS Items_au AFTER UPDATE ON Items BEGIN
              INSERT INTO ItemsFts(ItemsFts, rowid, Title, Url, Description, Tags)
              VALUES ('delete', old.RowId, old.Title, old.Url, old.Description, old.Tags);
              INSERT INTO ItemsFts(rowid, Title, Url, Description, Tags)
              VALUES (new.RowId, new.Title, new.Url, new.Description, new.Tags);
            END
            """).ConfigureAwait(false);

        _db = db;
        _log.Debug("store", $"Opened encrypted store at {_dbPath}");
    }

    private SQLiteAsyncConnection Db =>
        _db ?? throw new InvalidOperationException("Call InitializeAsync first.");

    public async Task ReplaceAllAsync(
        List<BookmarkNode> roots, string encryptedBlob, string lastUpdated, string? version,
        long? revision = null, string? hash = null)
    {
        var db = Db;
        await db.RunInTransactionAsync(tran =>
        {
            tran.Execute("DELETE FROM Items");
            int order = 0;
            foreach (var root in roots)
            {
                InsertNode(tran, root, parentRowId: null, ref order);
            }

            tran.InsertOrReplace(new BlobRow { Key = BlobBookmarks, Value = encryptedBlob });
            tran.InsertOrReplace(new MetaRow { Key = MetaLastUpdated, Value = lastUpdated });
            tran.InsertOrReplace(new MetaRow { Key = MetaVersion, Value = version });
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "0" });
            // v1.2 meta: stored when the server speaks it, cleared otherwise
            // so a legacy pull never leaves a stale revision behind.
            if (revision.HasValue)
            {
                tran.InsertOrReplace(new MetaRow { Key = MetaRevision, Value = revision.Value.ToString() });
            }
            else
            {
                tran.Execute("DELETE FROM Meta WHERE Key = ?", MetaRevision);
            }

            if (hash is not null)
            {
                tran.InsertOrReplace(new MetaRow { Key = MetaHash, Value = hash });
            }
            else
            {
                tran.Execute("DELETE FROM Meta WHERE Key = ?", MetaHash);
            }
        }).ConfigureAwait(false);
        _log.Info("store", $"Mirror replaced: {CountNodes(roots)} nodes, lastUpdated={lastUpdated}");
    }

    /// <summary>Replaces local rows with a backup tree. Mirror meta (lastUpdated/version)
    /// is kept and the mirror is marked dirty, so the next auto sync pushes the
    /// restored tree (or surfaces a real conflict) instead of pulling over it.</summary>
    public async Task RestoreLocalAsync(List<BookmarkNode> roots)
    {
        var db = Db;
        await db.RunInTransactionAsync(tran =>
        {
            tran.Execute("DELETE FROM Items");
            int order = 0;
            foreach (var root in roots)
            {
                InsertNode(tran, root, parentRowId: null, ref order);
            }

            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
        }).ConfigureAwait(false);
        _log.Info("store", $"Local restore: {CountNodes(roots)} nodes, marked dirty");
    }

    /// <summary>Gives every node of a new subtree without an id a fresh NodeId
    /// (root first), so a folder added together with its first child keeps
    /// stable identities for both.</summary>
    private static void AssignMissingIds(BookmarkNode node, ref int nextId)
    {
        node.Id ??= nextId++;
        if (node.Children is not null)
        {
            foreach (var child in node.Children)
            {
                AssignMissingIds(child, ref nextId);
            }
        }
    }

    /// <summary>New nodes get dateAdded = now; a date already set (e.g. copied
    /// from another tree) is kept.</summary>
    private static void StampMissingDates(BookmarkNode node, long nowMs)
    {
        node.DateAdded ??= nowMs;
        if (node.Children is not null)
        {
            foreach (var child in node.Children)
            {
                StampMissingDates(child, nowMs);
            }
        }
    }

    private static void InsertNode(SQLiteConnection tran, BookmarkNode node, int? parentRowId, ref int order)
    {
        var row = new ItemRow
        {
            NodeId = node.Id,
            ParentRowId = parentRowId,
            Title = node.Title,
            Url = node.Url,
            Description = string.IsNullOrWhiteSpace(node.Description) ? null : node.Description,
            Tags = node.Tags is { Count: > 0 } ? string.Join(TagSep, node.Tags) : null,
            DateAdded = node.DateAdded,
            IsFolder = node.Children is not null,
            SortOrder = order++
        };
        tran.Insert(row);
        if (node.Children is not null)
        {
            foreach (var child in node.Children)
            {
                InsertNode(tran, child, row.RowId, ref order);
            }
        }
    }

    public async Task<(List<BookmarkNode> Roots, string? Blob, string? LastUpdated, string? Version)> LoadMirrorAsync()
    {
        var db = Db;
        var rows = await db.Table<ItemRow>().OrderBy(r => r.SortOrder).ToListAsync().ConfigureAwait(false);
        var roots = rows.Where(r => r.ParentRowId is null).ToList();
        var byParent = rows.Where(r => r.ParentRowId is not null)
            .GroupBy(r => r.ParentRowId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        List<BookmarkNode> Build(int? parent)
        {
            var kids = parent is null ? roots : byParent.GetValueOrDefault(parent.Value, []);
            return kids.Select(ToNode).ToList();
        }

        BookmarkNode ToNode(ItemRow r) => new()
        {
            Id = r.NodeId,
            Title = r.Title,
            Url = r.Url,
            Description = r.Description,
            Tags = r.Tags is null ? null : r.Tags.Split(TagSep).ToList(),
            DateAdded = r.DateAdded,
            Children = r.IsFolder ? Build(r.RowId) : null
        };

        var blob = (await db.FindAsync<BlobRow>(BlobBookmarks).ConfigureAwait(false))?.Value;
        var meta = await db.Table<MetaRow>().ToListAsync().ConfigureAwait(false);
        string? Get(string k) => meta.FirstOrDefault(m => m.Key == k)?.Value;
        return (Build(null), blob, Get(MetaLastUpdated), Get(MetaVersion));
    }

    public async Task<List<SearchHit>> SearchAsync(string query, int limit = 50)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        // AND of prefix terms across all indexed columns (mirrors keyword-AND search).
        var terms = query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*");
        var match = string.Join(" AND ", terms);

        var rows = await Db.QueryAsync<ItemRow>(
            "SELECT i.* FROM ItemsFts f JOIN Items i ON i.RowId = f.rowid " +
            "WHERE ItemsFts MATCH ? ORDER BY bm25(ItemsFts) LIMIT ?",
            match, limit).ConfigureAwait(false);

        if (rows.Count == 0 && query.Length >= 4)
        {
            // Mid-token URL fragments (e.g. "othyku") never match FTS tokens: LIKE fallback.
            rows = await Db.Table<ItemRow>()
                .Where(r => r.Url != null && r.Url.Contains(query))
                .Take(limit).ToListAsync().ConfigureAwait(false);
        }

        var paths = await GetFolderPathsAsync().ConfigureAwait(false);
        return rows.Select((r, i) => new SearchHit(
            r.RowId, r.NodeId, r.Title, r.Url, r.Description, r.Tags?.Replace($"{TagSep}", ", "),
            r.IsFolder, paths.GetValueOrDefault(r.RowId, string.Empty), i)).ToList();
    }

    /// <summary>Bookmark URLs appearing more than once (separators excluded),
    /// each with its rows in display order. For the duplicates cleanup page.</summary>
    public async Task<List<DuplicateGroup>> GetDuplicateUrlsAsync()
    {
        var dup = await Db.QueryAsync<DupUrlRow>(
            "SELECT Url AS Value FROM Items WHERE Url IS NOT NULL AND Url != ? GROUP BY Url HAVING COUNT(*) > 1",
            Core.XbsConstants.SeparatorUrl).ConfigureAwait(false);
        var urls = dup.Select(d => d.Value).Where(u => u is not null).Cast<string>().ToList();
        if (urls.Count == 0)
        {
            return [];
        }

        var placeholders = string.Join(",", Enumerable.Repeat("?", urls.Count));
        var rows = await Db.QueryAsync<ItemRow>(
            $"SELECT * FROM Items WHERE Url IN ({placeholders}) ORDER BY Url, SortOrder",
            urls.Cast<object>().ToArray()).ConfigureAwait(false);
        return rows.GroupBy(r => r.Url!)
            .Select(g => new DuplicateGroup(g.Key, g.ToList()))
            .ToList();
    }

    private sealed class DupUrlRow
    {
        public string? Value { get; set; }
    }

    public async Task<List<ItemRow>> GetChildrenAsync(int? parentRowId) =>
        await Db.QueryAsync<ItemRow>(
            "SELECT * FROM Items WHERE ParentRowId IS ? ORDER BY SortOrder", parentRowId)
            .ConfigureAwait(false);

    /// <summary>Newest bookmarks first (folders and separators excluded). The
    /// sync format has no creation date: ids are assigned as MAX+1 when an
    /// item is added, so the highest NodeId is the most recently added.</summary>
    public async Task<List<ItemRow>> GetRecentBookmarksAsync(int limit) =>
        await Db.QueryAsync<ItemRow>(
            "SELECT * FROM Items WHERE IsFolder = 0 AND NodeId IS NOT NULL AND Url IS NOT NULL AND Url != ? " +
            "ORDER BY NodeId DESC LIMIT ?", Core.XbsConstants.SeparatorUrl, limit)
            .ConfigureAwait(false);

    public async Task<ItemRow?> GetNodeAsync(int rowId) =>
        await Db.FindAsync<ItemRow>(rowId).ConfigureAwait(false);

    /// <summary>Folder chain from top level down to the node's parent (for "go to location").</summary>
    public async Task<List<ItemRow>> GetAncestorChainAsync(int rowId)
    {
        var db = Db;
        var chain = new List<ItemRow>();
        var node = await db.FindAsync<ItemRow>(rowId).ConfigureAwait(false);
        var parent = node?.ParentRowId;
        while (parent is not null)
        {
            var folder = await db.FindAsync<ItemRow>(parent.Value).ConfigureAwait(false);
            if (folder is null)
            {
                break;
            }

            chain.Insert(0, folder);
            parent = folder.ParentRowId;
        }

        return chain;
    }

    public async Task<List<ItemRow>> GetByNodeIdsAsync(IEnumerable<int> nodeIds)
    {
        var ids = nodeIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var placeholders = string.Join(",", Enumerable.Repeat("?", ids.Count));
        return await Db.QueryAsync<ItemRow>(
            $"SELECT * FROM Items WHERE NodeId IN ({placeholders})", ids.Cast<object>().ToArray())
            .ConfigureAwait(false);
    }

    public async Task<int?> GetRowIdByNodeIdAsync(int nodeId) =>
        (await Db.QueryAsync<ItemRow>("SELECT * FROM Items WHERE NodeId = ? LIMIT 1", nodeId)
            .ConfigureAwait(false)).FirstOrDefault()?.RowId;

    /// <summary>Adds a node under the folder with the given stable NodeId
    /// (null = top level). Parent resolution + insert + dirty happen in one
    /// transaction, so a concurrent push/pull rekey cannot misplace the node.</summary>
    public async Task<int> AddNodeByParentNodeIdAsync(int? parentNodeId, BookmarkNode node)
    {
        var db = Db;
        int rowId = 0;
        await db.RunInTransactionAsync(tran =>
        {
            int? parentRowId = null;
            if (parentNodeId is not null)
            {
                parentRowId = tran.Query<ItemRow>("SELECT * FROM Items WHERE NodeId = ? LIMIT 1", parentNodeId.Value)
                    .FirstOrDefault()?.RowId;
            }

            int nextId = (tran.ExecuteScalar<int?>("SELECT MAX(NodeId) FROM Items") ?? 0) + 1;
            AssignMissingIds(node, ref nextId);
            StampMissingDates(node, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            int order = (tran.ExecuteScalar<int?>(
                parentRowId is null
                    ? "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId IS NULL"
                    : "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId = ?", parentRowId) ?? -1) + 1;
            InsertNode(tran, node, parentRowId, ref order);
            rowId = tran.ExecuteScalar<int>(
                "SELECT RowId FROM Items WHERE NodeId = ? ORDER BY RowId DESC LIMIT 1", node.Id);
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
        }).ConfigureAwait(false);
        _log.Debug("store", $"Node added under parentNode={parentNodeId}");
        return rowId;
    }

    /// <summary>Updates a node resolved by stable NodeId (resolve + update +
    /// dirty in one transaction).</summary>
    public async Task UpdateNodeByNodeIdAsync(int nodeId, string? title, string? url, string? description, List<string>? tags)
    {
        var db = Db;
        await db.RunInTransactionAsync(tran =>
        {
            var row = tran.Query<ItemRow>("SELECT * FROM Items WHERE NodeId = ? LIMIT 1", nodeId).FirstOrDefault()
                ?? throw new InvalidOperationException($"Bookmark node {nodeId} not found.");
            row.Title = title;
            row.Url = url;
            row.Description = string.IsNullOrWhiteSpace(description) ? null : description;
            row.Tags = tags is { Count: > 0 } ? string.Join(TagSep, tags) : null;
            tran.Update(row);
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
        }).ConfigureAwait(false);
    }

    /// <summary>Deletes a node (by stable NodeId) and its whole subtree.
    /// Returns the parent's stable NodeId + subtree for undo.</summary>
    public async Task<(int? ParentNodeId, List<BookmarkNode> Subtree)> DeleteNodeByNodeIdAsync(int nodeId)
    {
        var db = Db;
        int? parentNodeId = null;
        List<BookmarkNode> subtree = [];
        await db.RunInTransactionAsync(tran =>
        {
            var target = tran.Query<ItemRow>("SELECT * FROM Items WHERE NodeId = ? LIMIT 1", nodeId).FirstOrDefault()
                ?? throw new InvalidOperationException($"Bookmark node {nodeId} not found.");
            var rowId = target.RowId;
            parentNodeId = target.ParentRowId is null
                ? null
                : tran.Find<ItemRow>(target.ParentRowId.Value)?.NodeId;

            var rows = tran.Query<ItemRow>("SELECT * FROM Items");
            var kids = rows.Where(r => r.ParentRowId is not null)
                .GroupBy(r => r.ParentRowId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(r => r.RowId).ToList());

            var doomed = new List<int> { rowId };
            var queue = new Queue<int>([rowId]);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (kids.TryGetValue(cur, out var c))
                {
                    doomed.AddRange(c);
                    foreach (var k in c)
                    {
                        queue.Enqueue(k);
                    }
                }
            }

            var byId = rows.ToDictionary(r => r.RowId);
            BookmarkNode BuildRow(int id)
            {
                var r = byId[id];
                return new BookmarkNode
                {
                    Id = r.NodeId,
                    Title = r.Title,
                    Url = r.Url,
                    Description = r.Description,
                    Tags = r.Tags is null ? null : r.Tags.Split(TagSep).ToList(),
                    DateAdded = r.DateAdded,
                    Children = r.IsFolder && kids.TryGetValue(id, out var c)
                        ? c.Select(BuildRow).ToList()
                        : null
                };
            }

            subtree = [BuildRow(rowId)];

            foreach (var id in doomed)
            {
                tran.Execute("DELETE FROM Items WHERE RowId = ?", id);
            }

            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
            _log.Debug("store", $"Deleted node {nodeId} (+{doomed.Count - 1} descendants)");
        }).ConfigureAwait(false);

        return (parentNodeId, subtree);
    }

    /// <summary>Moves a node (by stable NodeId) under another node (by stable
    /// NodeId, null = top level). Missing parent falls back to top level.</summary>
    public async Task MoveNodeByNodeIdAsync(int nodeId, int? newParentNodeId)
    {
        var db = Db;
        await db.RunInTransactionAsync(tran =>
        {
            var node = tran.Query<ItemRow>("SELECT * FROM Items WHERE NodeId = ? LIMIT 1", nodeId).FirstOrDefault()
                ?? throw new InvalidOperationException($"Bookmark node {nodeId} not found.");
            int? newParentRowId = null;
            if (newParentNodeId is not null)
            {
                newParentRowId = tran.Query<ItemRow>("SELECT * FROM Items WHERE NodeId = ? LIMIT 1", newParentNodeId.Value)
                    .FirstOrDefault()?.RowId;
            }

            // Refuse to move a folder into its own subtree.
            var ancestor = newParentRowId;
            while (ancestor is not null)
            {
                if (ancestor == node.RowId)
                {
                    throw new InvalidOperationException(Loc.Get(Str.ErrFolderIntoItself));
                }

                ancestor = tran.Find<ItemRow>(ancestor.Value)?.ParentRowId;
            }

            int order = (tran.ExecuteScalar<int?>(
                newParentRowId is null
                    ? "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId IS NULL"
                    : "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId = ?", newParentRowId) ?? -1) + 1;
            node.ParentRowId = newParentRowId;
            node.SortOrder = order;
            tran.Update(node);
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
        }).ConfigureAwait(false);
        _log.Debug("store", $"Moved node {nodeId} to parentNode={newParentNodeId}");
    }

    /// <summary>Restores a subtree under the folder with the given stable
    /// NodeId (null = top level). Missing parent falls back to top level.</summary>
    public async Task RestoreSubtreeByNodeIdAsync(int? parentNodeId, List<BookmarkNode> subtree)
    {
        var db = Db;
        await db.RunInTransactionAsync(tran =>
        {
            int? parentRowId = null;
            if (parentNodeId is not null)
            {
                parentRowId = tran.Query<ItemRow>("SELECT * FROM Items WHERE NodeId = ? LIMIT 1", parentNodeId.Value)
                    .FirstOrDefault()?.RowId;
            }

            int order = (tran.ExecuteScalar<int?>(
                parentRowId is null
                    ? "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId IS NULL"
                    : "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId = ?", parentRowId) ?? -1) + 1;
            foreach (var node in subtree)
            {
                InsertNode(tran, node, parentRowId, ref order);
            }

            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
        }).ConfigureAwait(false);
    }

    public async Task<int> AddNodeAsync(int? parentRowId, BookmarkNode node)
    {
        var db = Db;
        int rowId = 0;
        await db.RunInTransactionAsync(tran =>
        {
            int nextId = (tran.ExecuteScalar<int?>("SELECT MAX(NodeId) FROM Items") ?? 0) + 1;
            AssignMissingIds(node, ref nextId);
            StampMissingDates(node, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            int order = (tran.ExecuteScalar<int?>(
                parentRowId is null
                    ? "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId IS NULL"
                    : "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId = ?", parentRowId) ?? -1) + 1;
            InsertNode(tran, node, parentRowId, ref order);
            // NodeIds are unique among live rows: locate the inserted root.
            rowId = tran.ExecuteScalar<int>(
                "SELECT RowId FROM Items WHERE NodeId = ? ORDER BY RowId DESC LIMIT 1", node.Id);
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
        }).ConfigureAwait(false);
        _log.Debug("store", $"Node added under parent={parentRowId}");
        return rowId;
    }

    public async Task UpdateNodeAsync(int rowId, string? title, string? url, string? description, List<string>? tags)
    {
        var db = Db;
        await db.RunInTransactionAsync(tran =>
        {
            var node = tran.Find<ItemRow>(rowId)
                ?? throw new InvalidOperationException($"Bookmark row {rowId} not found.");
            node.Title = title;
            node.Url = url;
            node.Description = string.IsNullOrWhiteSpace(description) ? null : description;
            node.Tags = tags is { Count: > 0 } ? string.Join(TagSep, tags) : null;
            tran.Update(node);
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
        }).ConfigureAwait(false);
    }

    /// <summary>Deletes many nodes by stable NodeId in ONE transaction
    /// (resolve + delete atomically, so a concurrent push/pull rekey cannot
    /// strand half the batch). Missing ids are already gone: skipped.
    /// Returns the number of deleted rows. One caller = one push.</summary>
    public async Task<int> DeleteNodesByNodeIdsAsync(IEnumerable<int> nodeIds)
    {
        var ids = nodeIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        var db = Db;
        int deleted = 0;
        await db.RunInTransactionAsync(tran =>
        {
            var placeholders = string.Join(",", Enumerable.Repeat("?", ids.Count));
            var targets = tran.Query<ItemRow>(
                $"SELECT * FROM Items WHERE NodeId IN ({placeholders})", ids.Cast<object>().ToArray());
            if (targets.Count == 0)
            {
                return;
            }

            var rows = tran.Query<ItemRow>("SELECT * FROM Items");
            var kids = rows.Where(r => r.ParentRowId is not null)
                .GroupBy(r => r.ParentRowId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(r => r.RowId).ToList());

            var doomed = new HashSet<int>();
            var queue = new Queue<int>(targets.Select(r => r.RowId));
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!doomed.Add(cur))
                {
                    continue;
                }

                if (kids.TryGetValue(cur, out var c))
                {
                    foreach (var k in c)
                    {
                        queue.Enqueue(k);
                    }
                }
            }

            foreach (var id in doomed)
            {
                tran.Execute("DELETE FROM Items WHERE RowId = ?", id);
            }

            deleted = doomed.Count;
            if (deleted > 0)
            {
                tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
            }
        }).ConfigureAwait(false);

        _log.Debug("store", $"Batch deleted {deleted} row(s)");
        return deleted;
    }

    /// <summary>Deletes a node and its whole subtree. Returns the parent + subtree for undo.</summary>
    public async Task<(int? ParentRowId, List<BookmarkNode> Subtree)> DeleteNodeAsync(int rowId)
    {
        var db = Db;
        int? parentRowId = null;
        List<BookmarkNode> subtree = [];
        await db.RunInTransactionAsync(tran =>
        {
            var target = tran.Find<ItemRow>(rowId)
                ?? throw new InvalidOperationException($"Bookmark row {rowId} not found.");
            parentRowId = target.ParentRowId;

            var rows = tran.Query<ItemRow>("SELECT * FROM Items");
            var kids = rows.Where(r => r.ParentRowId is not null)
                .GroupBy(r => r.ParentRowId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(r => r.RowId).ToList());

            var doomed = new List<int> { rowId };
            var queue = new Queue<int>([rowId]);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (kids.TryGetValue(cur, out var c))
                {
                    doomed.AddRange(c);
                    foreach (var k in c)
                    {
                        queue.Enqueue(k);
                    }
                }
            }

            var byId = rows.ToDictionary(r => r.RowId);
            BookmarkNode BuildRow(int id)
            {
                var r = byId[id];
                return new BookmarkNode
                {
                    Id = r.NodeId,
                    Title = r.Title,
                    Url = r.Url,
                    Description = r.Description,
                    Tags = r.Tags is null ? null : r.Tags.Split(TagSep).ToList(),
                    DateAdded = r.DateAdded,
                    Children = r.IsFolder && kids.TryGetValue(id, out var c)
                        ? c.Select(BuildRow).ToList()
                        : null
                };
            }

            subtree = [BuildRow(rowId)];

            foreach (var id in doomed)
            {
                tran.Execute("DELETE FROM Items WHERE RowId = ?", id);
            }

            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
            _log.Debug("store", $"Deleted node {rowId} (+{doomed.Count - 1} descendants)");
        }).ConfigureAwait(false);

        return (parentRowId, subtree);
    }

    public async Task RestoreSubtreeAsync(int? parentRowId, List<BookmarkNode> subtree)
    {
        var db = Db;
        await db.RunInTransactionAsync(tran =>
        {
            int order = (tran.ExecuteScalar<int?>(
                parentRowId is null
                    ? "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId IS NULL"
                    : "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId = ?", parentRowId) ?? -1) + 1;
            foreach (var node in subtree)
            {
                InsertNode(tran, node, parentRowId, ref order);
            }

            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
        }).ConfigureAwait(false);
    }

    public async Task<List<ItemRow>> GetAllFoldersAsync() =>
        await Db.QueryAsync<ItemRow>("SELECT * FROM Items WHERE IsFolder ORDER BY Title")
            .ConfigureAwait(false);

    public async Task MoveNodeAsync(int rowId, int? newParentRowId)
    {
        var db = Db;
        var node = await db.FindAsync<ItemRow>(rowId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Bookmark row {rowId} not found.");

        // Refuse to move a folder into its own subtree.
        var ancestor = newParentRowId;
        while (ancestor is not null)
        {
            if (ancestor == rowId)
            {
                throw new InvalidOperationException(Loc.Get(Str.ErrFolderIntoItself));
            }

            ancestor = (await db.FindAsync<ItemRow>(ancestor.Value).ConfigureAwait(false))?.ParentRowId;
        }

        await db.RunInTransactionAsync(tran =>
        {
            int order = (tran.ExecuteScalar<int?>(
                newParentRowId is null
                    ? "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId IS NULL"
                    : "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId = ?", newParentRowId) ?? -1) + 1;
            node.ParentRowId = newParentRowId;
            node.SortOrder = order;
            tran.Update(node);
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
        }).ConfigureAwait(false);
        _log.Debug("store", $"Moved node {rowId} to parent={newParentRowId}");
    }

    public async Task<ContentStats> GetStatsAsync()
    {
        var rows = await Db.Table<ItemRow>().ToListAsync().ConfigureAwait(false);
        var tags = rows.Where(r => r.Tags is not null)
            .SelectMany(r => r.Tags!.Split(TagSep)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var urls = rows.Where(r => !r.IsFolder && r.Url is not null && r.Url != XbsConstants.SeparatorUrl)
            .GroupBy(r => r.Url).Count(g => g.Count() > 1);

        int Depth(ItemRow r, Dictionary<int, ItemRow> byId, int d)
        {
            if (r.ParentRowId is null || !byId.TryGetValue(r.ParentRowId.Value, out var p))
            {
                return d;
            }

            return Depth(p, byId, d + 1);
        }

        var byId = rows.ToDictionary(r => r.RowId);
        int maxDepth = rows.Count == 0 ? 0 : rows.Max(r => Depth(r, byId, 0));

        return new ContentStats(
            rows.Count(r => r.IsFolder),
            rows.Count(r => !r.IsFolder && r.Url != XbsConstants.SeparatorUrl),
            rows.Count(r => r.Url == XbsConstants.SeparatorUrl),
            tags, maxDepth, urls);
    }

    /// <summary>
    /// Bookmarks inside each folder, counted through every subfolder
    /// (separators excluded). Folders with no bookmarks map to 0.
    /// </summary>
    public async Task<Dictionary<int, int>> GetFolderBookmarkCountsAsync()
    {
        var rows = await Db.QueryAsync<ItemRow>(
            "SELECT RowId, ParentRowId, IsFolder, Url FROM Items").ConfigureAwait(false);
        var byId = rows.ToDictionary(r => r.RowId);
        var counts = rows.Where(r => r.IsFolder).ToDictionary(r => r.RowId, _ => 0);
        foreach (var r in rows)
        {
            if (r.IsFolder || r.Url == XbsConstants.SeparatorUrl)
            {
                continue;
            }

            // Walk up the ancestors; the hop limit guards against a corrupt cycle.
            var parent = r.ParentRowId;
            for (var hops = 0; parent is int p && byId.TryGetValue(p, out var f) && hops < 1024; hops++)
            {
                counts[p] = counts.GetValueOrDefault(p) + 1;
                parent = f.ParentRowId;
            }
        }

        return counts;
    }

    public async Task<string?> GetMetaAsync(string key) =>
        (await Db.FindAsync<MetaRow>(key).ConfigureAwait(false))?.Value;

    public async Task SetMetaAsync(string key, string? value) =>
        await Db.InsertOrReplaceAsync(new MetaRow { Key = key, Value = value }).ConfigureAwait(false);

    public long GetFileSize() => new FileInfo(_dbPath).Length;

    /// <summary>
    /// Stable pin key for every row: bookmarks by URL ("u:…"), folders by the raw
    /// title path from the root ("f:…"). NodeIds are not stable — any client that
    /// rewrites the sync renumbers them — so pins must never be keyed on them.
    /// </summary>
    public async Task<Dictionary<int, string>> GetPinKeysAsync()
    {
        var all = await Db.Table<ItemRow>().ToListAsync().ConfigureAwait(false);
        return PinKeys(all);
    }

    // Bookmark keys are "u:" + URL + this + title: the title only picks the pinned
    // copy among duplicates; the URL alone still matches after a title edit.
    private const string PinTitleSep = "\u001e";

    internal static Dictionary<int, string> PinKeys(List<ItemRow> all)
    {
        var byId = all.ToDictionary(r => r.RowId);
        var keys = new Dictionary<int, string>();
        foreach (var r in all)
        {
            if (!r.IsFolder)
            {
                keys[r.RowId] = r.Url is { Length: > 0 } url ? "u:" + url + PinTitleSep + r.Title : "n:" + r.RowId;
                continue;
            }

            var parts = new Stack<string>();
            for (ItemRow? cur = r; cur is not null; cur = cur.ParentRowId is int pid && byId.TryGetValue(pid, out var p) ? p : null)
            {
                parts.Push(cur.Title ?? string.Empty);
            }

            keys[r.RowId] = "f:" + string.Join("\u001f", parts);
        }

        return keys;
    }

    private static string UrlPart(string key) =>
        key.StartsWith("u:", StringComparison.Ordinal) && key.IndexOf(PinTitleSep, StringComparison.Ordinal) is int i and >= 0
            ? key[..i] : key;

    /// <summary>Rows for the given pin keys, in key order; one row per key (first copy).</summary>
    public async Task<(List<ItemRow> Rows, Dictionary<int, string> KeyByRow)> GetPinnedAsync(IReadOnlyList<string> pinKeys)
    {
        var all = await Db.Table<ItemRow>().OrderBy(r => r.RowId).ToListAsync().ConfigureAwait(false);
        var keyByRow = PinKeys(all);
        var firstByKey = new Dictionary<string, ItemRow>();
        var firstByUrl = new Dictionary<string, ItemRow>();
        foreach (var r in all)
        {
            var key = keyByRow[r.RowId];
            firstByKey.TryAdd(key, r);
            firstByUrl.TryAdd(UrlPart(key), r);
        }

        var rows = new List<ItemRow>();
        foreach (var k in pinKeys.Distinct())
        {
            var row = firstByKey.GetValueOrDefault(k) ?? firstByUrl.GetValueOrDefault(UrlPart(k));
            if (row is not null && !rows.Contains(row))
            {
                // Title edited: report the row under its stored key so it still shows as pinned.
                keyByRow[row.RowId] = k;
                rows.Add(row);
            }
        }

        return (rows, keyByRow);
    }

    public async Task<Dictionary<int, string>> GetFolderPathsAsync()
    {
        var paths = await Db.Table<ItemRow>().ToListAsync().ConfigureAwait(false);
        var byId = paths.ToDictionary(r => r.RowId);
        var cache = new Dictionary<int, string>();

        string PathOf(int rowId)
        {
            if (cache.TryGetValue(rowId, out var cached))
            {
                return cached;
            }

            var parts = new Stack<string>();
            var cur = byId[rowId];
            while (cur.ParentRowId is not null && byId.TryGetValue(cur.ParentRowId.Value, out var p))
            {
                if (p.DisplayTitle is { } name)
                {
                    parts.Push(name);
                }

                cur = p;
            }

            return cache[rowId] = string.Join(" / ", parts);
        }

        foreach (var r in paths)
        {
            PathOf(r.RowId);
        }

        return cache;
    }

    private static int CountNodes(List<BookmarkNode> roots)
    {
        int n = 0;
        var stack = new Stack<BookmarkNode>(roots);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            n++;
            if (cur.Children is not null)
            {
                foreach (var c in cur.Children)
                {
                    stack.Push(c);
                }
            }
        }

        return n;
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.CloseAsync().ConfigureAwait(false);
            _db = null;
        }
    }
}
