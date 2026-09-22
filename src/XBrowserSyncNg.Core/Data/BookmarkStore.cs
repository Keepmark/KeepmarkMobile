using SQLite;
using XBrowserSyncNg.Core.Logging;
using XBrowserSyncNg.Core.Models;

namespace XBrowserSyncNg.Core.Data;

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
    public bool IsFolder { get; set; }
    public int SortOrder { get; set; }
}

public sealed record SearchHit(
    int RowId, int? NodeId, string? Title, string? Url, string? Description,
    string? Tags, bool IsFolder, string FolderPath, double Rank);

public sealed record ContentStats(
    int Folders, int Bookmarks, int Separators, int DistinctTags,
    int MaxDepth, int DuplicateUrls);

/// <summary>
/// Encrypted local mirror (SQLCipher) + FTS5 search index.
/// The encryption key (64 hex chars) is supplied by the platform layer
/// (Android KeyStore); Core never stores it.
/// </summary>
public sealed class BookmarkStore : IAsyncDisposable
{
    public const string MetaLastUpdated = "lastUpdated";
    public const string MetaVersion = "syncVersion";
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
        List<BookmarkNode> roots, string encryptedBlob, string lastUpdated, string? version)
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
        }).ConfigureAwait(false);
        _log.Info("store", $"Mirror replaced: {CountNodes(roots)} nodes, lastUpdated={lastUpdated}");
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

    public async Task<List<ItemRow>> GetChildrenAsync(int? parentRowId) =>
        await Db.QueryAsync<ItemRow>(
            "SELECT * FROM Items WHERE ParentRowId IS ? ORDER BY SortOrder", parentRowId)
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

    public async Task<int> AddNodeAsync(int? parentRowId, BookmarkNode node)
    {
        var db = Db;
        int rowId = 0;
        await db.RunInTransactionAsync(tran =>
        {
            int nextId = (tran.ExecuteScalar<int?>("SELECT MAX(NodeId) FROM Items") ?? 0) + 1;
            node.Id ??= nextId;
            int order = (tran.ExecuteScalar<int?>(
                parentRowId is null
                    ? "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId IS NULL"
                    : "SELECT MAX(SortOrder) FROM Items WHERE ParentRowId = ?", parentRowId) ?? -1) + 1;
            InsertNode(tran, node, parentRowId, ref order);
            // NodeIds are unique among live rows: locate the inserted root.
            rowId = tran.ExecuteScalar<int>(
                "SELECT RowId FROM Items WHERE NodeId = ? ORDER BY RowId DESC LIMIT 1", node.Id);
        }).ConfigureAwait(false);
        _log.Debug("store", $"Node added under parent={parentRowId}");
        return rowId;
    }

    public async Task UpdateNodeAsync(int rowId, string? title, string? url, string? description, List<string>? tags)
    {
        var node = await Db.FindAsync<ItemRow>(rowId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Bookmark row {rowId} not found.");
        node.Title = title;
        node.Url = url;
        node.Description = string.IsNullOrWhiteSpace(description) ? null : description;
        node.Tags = tags is { Count: > 0 } ? string.Join(TagSep, tags) : null;
        await Db.UpdateAsync(node).ConfigureAwait(false);
    }

    /// <summary>Deletes a node and its whole subtree. Returns the parent + subtree for undo.</summary>
    public async Task<(int? ParentRowId, List<BookmarkNode> Subtree)> DeleteNodeAsync(int rowId)
    {
        var db = Db;
        var target = await db.FindAsync<ItemRow>(rowId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Bookmark row {rowId} not found.");

        var rows = await db.Table<ItemRow>().ToListAsync().ConfigureAwait(false);
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
                Children = r.IsFolder && kids.TryGetValue(id, out var c)
                    ? c.Select(BuildRow).ToList()
                    : null
            };
        }

        var subtree = new List<BookmarkNode> { BuildRow(rowId) };

        await db.RunInTransactionAsync(tran =>
        {
            foreach (var id in doomed)
            {
                tran.Execute("DELETE FROM Items WHERE RowId = ?", id);
            }
        }).ConfigureAwait(false);

        _log.Debug("store", $"Deleted node {rowId} (+{doomed.Count - 1} descendants)");
        return (target.ParentRowId, subtree);
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
                throw new InvalidOperationException("Cannot move a folder into itself.");
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

    public async Task<string?> GetMetaAsync(string key) =>
        (await Db.FindAsync<MetaRow>(key).ConfigureAwait(false))?.Value;

    public async Task SetMetaAsync(string key, string? value) =>
        await Db.InsertOrReplaceAsync(new MetaRow { Key = key, Value = value }).ConfigureAwait(false);

    public long GetFileSize() => new FileInfo(_dbPath).Length;

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
                if (p.Title is not null)
                {
                    parts.Push(p.Title);
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
