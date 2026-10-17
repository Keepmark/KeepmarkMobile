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
public sealed partial class BookmarkStore : IAsyncDisposable
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
    private long _contentVersion;

    /// <summary>Changes whenever item content changes; consumers may invalidate derived UI data.</summary>
    public long ContentVersion => Interlocked.Read(ref _contentVersion);

    public BookmarkStore(string dbPath, string keyHex, IXbsLogger log)
    {
        _dbPath = dbPath;
        _keyHex = keyHex;
        _log = log;
    }

    public async Task InitializeAsync()
    {
        SqlCipherRuntime.Initialize();
        var db = new SQLiteAsyncConnection(_dbPath);
        // NOTE: PRAGMA key returns a row under SQLCipher, so ExecuteScalar (not Execute)
        // must be used — sqlite-net throws "not an error" on row-returning pragmas.
        await db.ExecuteScalarAsync<int>($"PRAGMA key = \"x'{_keyHex}'\"").ConfigureAwait(false);
        // Fail closed if a mispackaged native lacks encryption; PRAGMA key alone
        // is silently ignored by plaintext SQLite.
        var cipherVersion = await db.ExecuteScalarAsync<string>("PRAGMA cipher_version").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cipherVersion))
        {
            await db.CloseAsync().ConfigureAwait(false);
            throw new InvalidOperationException(Loc.Get(Str.ErrEncryptedDatabaseRequired));
        }
        // Verify key: unreadable DB throws here on first real access.
        await db.ExecuteScalarAsync<int>("SELECT count(*) FROM sqlite_master").ConfigureAwait(false);

        await db.CreateTablesAsync<MetaRow, BlobRow, ItemRow>().ConfigureAwait(false);
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_Items_NodeId ON Items(NodeId)").ConfigureAwait(false);
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_Items_Parent_Order ON Items(ParentRowId, SortOrder)").ConfigureAwait(false);
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_Items_Url_Order ON Items(Url, SortOrder)").ConfigureAwait(false);
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
            Interlocked.Increment(ref _contentVersion);
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

    /// <summary>Acknowledges an upload of the gated local tree without rewriting Items/FTS.
    /// The sync engine must serialize edits until this transaction completes.</summary>
    public async Task<bool> AcknowledgePushAsync(string encryptedBlob, string lastUpdated, string? version,
        long? revision = null, string? hash = null)
    {
        bool changed = false;
        await Db.RunInTransactionAsync(tran =>
        {
            foreach (var row in tran.Query<ItemRow>("SELECT RowId, Description FROM Items WHERE Description IS NOT NULL"))
            {
                var description = string.IsNullOrWhiteSpace(row.Description) ? null : row.Description;
                if (description?.Length > XbsConstants.DescriptionMaxLength)
                {
                    description = description[..XbsConstants.DescriptionMaxLength];
                }

                if (description != row.Description)
                {
                    tran.Execute("UPDATE Items SET Description = ? WHERE RowId = ?", description, row.RowId);
                    changed = true;
                }
            }

            tran.InsertOrReplace(new BlobRow { Key = BlobBookmarks, Value = encryptedBlob });
            tran.InsertOrReplace(new MetaRow { Key = MetaLastUpdated, Value = lastUpdated });
            tran.InsertOrReplace(new MetaRow { Key = MetaVersion, Value = version });
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "0" });
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

            if (changed)
            {
                Interlocked.Increment(ref _contentVersion);
            }
        }).ConfigureAwait(false);
        return changed;
    }

    /// <summary>Replaces local rows with a backup tree. Mirror meta (lastUpdated/version)
    /// is kept and the mirror is marked dirty, so the next auto sync pushes the
    /// restored tree (or surfaces a real conflict) instead of pulling over it.</summary>
    public async Task RestoreLocalAsync(List<BookmarkNode> roots)
    {
        var db = Db;
        await db.RunInTransactionAsync(tran =>
        {
            Interlocked.Increment(ref _contentVersion);
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

    public async Task<(List<BookmarkNode> Roots, string? Blob, string? LastUpdated, string? Version)> LoadMirrorAsync(bool includeBlob = true)
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

        var blob = includeBlob ? (await db.FindAsync<BlobRow>(BlobBookmarks).ConfigureAwait(false))?.Value : null;
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

        var paths = await GetFolderPathsAsync(rows.Select(r => r.RowId)).ConfigureAwait(false);
        return rows.Select((r, i) => new SearchHit(
            r.RowId, r.NodeId, r.Title, r.Url, r.Description, r.Tags?.Replace($"{TagSep}", ", "),
            r.IsFolder, paths.GetValueOrDefault(r.RowId, string.Empty), i)).ToList();
    }

    /// <summary>Bookmark URLs appearing more than once (separators excluded),
    /// each with its rows in display order. For the duplicates cleanup page.</summary>
    public async Task<List<DuplicateGroup>> GetDuplicateUrlsAsync()
    {
        var rows = await Db.QueryAsync<ItemRow>(
            "SELECT * FROM Items WHERE Url IN (SELECT Url FROM Items WHERE Url IS NOT NULL AND Url != ? " +
            "GROUP BY Url HAVING COUNT(*) > 1) ORDER BY Url, SortOrder",
            Core.XbsConstants.SeparatorUrl).ConfigureAwait(false);
        return rows.GroupBy(r => r.Url!)
            .Select(g => new DuplicateGroup(g.Key, g.ToList()))
            .ToList();
    }

    /// <summary>Number of redundant bookmark copies, excluding the survivor of each duplicate group.</summary>
    public Task<int> CountDuplicateBookmarksAsync() => Db.ExecuteScalarAsync<int>(
        "SELECT COALESCE(SUM(n - 1), 0) FROM (SELECT COUNT(*) AS n FROM Items " +
        "WHERE Url IS NOT NULL AND Url != ? GROUP BY Url HAVING COUNT(*) > 1)", XbsConstants.SeparatorUrl);

    /// <summary>Bookmarks whose URL matches <paramref name="url"/> once both are
    /// normalized (<see cref="Sync.BookmarkMerge.NormalizeUrl"/>: scheme/host
    /// case, default port, fragment, tracking params, trailing slash). For the
    /// "already saved" hint in the edit sheet; display order.</summary>
    public async Task<List<ItemRow>> FindByNormalizedUrlAsync(string url)
    {
        var raw = url.Trim();
        if (raw.Length == 0)
        {
            return [];
        }

        var key = Sync.BookmarkMerge.NormalizeUrl(raw);
        // Pre-filter on the host (case-insensitive LIKE) so a large tree is not
        // normalized row by row; unparsable input scans every bookmark.
        var host = Uri.TryCreate(raw, UriKind.Absolute, out var u) ? u.Host : null;
        var rows = host is { Length: > 0 }
            ? await Db.QueryAsync<ItemRow>(
                "SELECT * FROM Items WHERE Url IS NOT NULL AND Url != ? AND Url LIKE ? ORDER BY SortOrder",
                Core.XbsConstants.SeparatorUrl, "%" + host + "%").ConfigureAwait(false)
            : await Db.QueryAsync<ItemRow>(
                "SELECT * FROM Items WHERE Url IS NOT NULL AND Url != ? ORDER BY SortOrder",
                Core.XbsConstants.SeparatorUrl).ConfigureAwait(false);
        return rows.Where(r => Sync.BookmarkMerge.NormalizeUrl(r.Url!) == key).ToList();
    }

    public async Task<List<ItemRow>> GetChildrenAsync(int? parentRowId) =>
        await Db.QueryAsync<ItemRow>(
            "SELECT * FROM Items WHERE ParentRowId IS ? ORDER BY SortOrder", parentRowId)
            .ConfigureAwait(false);

    /// <summary>Newest bookmarks first (folders and separators excluded), by
    /// dateAdded. Items without one (older clients) come after the dated ones;
    /// among them ids are assigned as MAX+1 on add, so the highest NodeId is
    /// the most recently added.</summary>
    public async Task<List<ItemRow>> GetRecentBookmarksAsync(int limit) =>
        await Db.QueryAsync<ItemRow>(
            "SELECT * FROM Items WHERE IsFolder = 0 AND NodeId IS NOT NULL AND Url IS NOT NULL AND Url != ? " +
            "ORDER BY DateAdded IS NULL, DateAdded DESC, NodeId DESC LIMIT ?", Core.XbsConstants.SeparatorUrl, limit)
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
            Interlocked.Increment(ref _contentVersion);
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
            Interlocked.Increment(ref _contentVersion);
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
            Interlocked.Increment(ref _contentVersion);
            var target = tran.Query<ItemRow>("SELECT * FROM Items WHERE NodeId = ? LIMIT 1", nodeId).FirstOrDefault()
                ?? throw new InvalidOperationException($"Bookmark node {nodeId} not found.");
            var rowId = target.RowId;
            parentNodeId = target.ParentRowId is null
                ? null
                : tran.Find<ItemRow>(target.ParentRowId.Value)?.NodeId;

            var rows = LoadSubtrees(tran, [rowId]);
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
                    Children = r.IsFolder
                        ? kids.TryGetValue(id, out var c) ? c.Select(BuildRow).ToList() : []
                        : null
                };
            }

            subtree = [BuildRow(rowId)];

            DeleteRows(tran, doomed);

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
            Interlocked.Increment(ref _contentVersion);
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
            Interlocked.Increment(ref _contentVersion);
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
            Interlocked.Increment(ref _contentVersion);
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
            Interlocked.Increment(ref _contentVersion);
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
            Interlocked.Increment(ref _contentVersion);
            var placeholders = string.Join(",", Enumerable.Repeat("?", ids.Count));
            var targets = tran.Query<ItemRow>(
                $"SELECT * FROM Items WHERE NodeId IN ({placeholders})", ids.Cast<object>().ToArray());
            if (targets.Count == 0)
            {
                return;
            }

            var rows = LoadSubtrees(tran, targets.Select(r => r.RowId));
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

            DeleteRows(tran, doomed);

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
            Interlocked.Increment(ref _contentVersion);
            var target = tran.Find<ItemRow>(rowId)
                ?? throw new InvalidOperationException($"Bookmark row {rowId} not found.");
            parentRowId = target.ParentRowId;

            var rows = LoadSubtrees(tran, [rowId]);
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
                    Children = r.IsFolder
                        ? kids.TryGetValue(id, out var c) ? c.Select(BuildRow).ToList() : []
                        : null
                };
            }

            subtree = [BuildRow(rowId)];

            DeleteRows(tran, doomed);

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
            Interlocked.Increment(ref _contentVersion);
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
            Interlocked.Increment(ref _contentVersion);
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

        var byId = rows.ToDictionary(r => r.RowId);
        var depths = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            if (depths.ContainsKey(row.RowId))
            {
                continue;
            }

            var chain = new List<int>();
            var visiting = new HashSet<int>();
            var cur = row;
            while (!depths.ContainsKey(cur.RowId) && visiting.Add(cur.RowId))
            {
                chain.Add(cur.RowId);
                if (cur.ParentRowId is not int parent || !byId.TryGetValue(parent, out cur))
                {
                    break;
                }
            }

            var depth = cur is not null && depths.TryGetValue(cur.RowId, out var known) ? known + 1 : 0;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                depths[chain[i]] = depth++;
            }
        }

        int maxDepth = depths.Count == 0 ? 0 : depths.Values.Max();

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
        var rows = await Db.QueryAsync<BookmarkCountRow>(
            "SELECT RowId, ParentRowId, IsFolder, " +
            "CASE WHEN IsFolder = 0 AND (Url IS NULL OR Url != ?) THEN 1 ELSE 0 END AS Bookmarks FROM Items",
            XbsConstants.SeparatorUrl).ConfigureAwait(false);
        var byId = rows.ToDictionary(r => r.RowId);
        foreach (var row in rows)
        {
            if (row.ParentRowId is int parent && byId.TryGetValue(parent, out var parentRow))
            {
                parentRow.PendingChildren++;
            }
        }

        // Leaf-to-root aggregation visits each edge once, independent of depth.
        // Corrupt cycles never reach the queue, so they cannot loop indefinitely.
        var queue = new Queue<BookmarkCountRow>(rows.Where(r => r.PendingChildren == 0));
        while (queue.TryDequeue(out var row))
        {
            if (row.ParentRowId is int parent && byId.TryGetValue(parent, out var parentRow))
            {
                parentRow.Bookmarks += row.Bookmarks;
                if (--parentRow.PendingChildren == 0)
                {
                    queue.Enqueue(parentRow);
                }
            }
        }

        var counts = rows.Where(r => r.IsFolder).ToDictionary(r => r.RowId, r => r.Bookmarks);
        return counts;
    }

    private sealed class BookmarkCountRow
    {
        public int RowId { get; set; }
        public int? ParentRowId { get; set; }
        public bool IsFolder { get; set; }
        public int Bookmarks { get; set; }
        [Ignore]
        public int PendingChildren { get; set; }
    }

    public async Task<string?> GetMetaAsync(string key) =>
        (await Db.FindAsync<MetaRow>(key).ConfigureAwait(false))?.Value;

    public async Task SetMetaAsync(string key, string? value) =>
        await Db.InsertOrReplaceAsync(new MetaRow { Key = key, Value = value }).ConfigureAwait(false);

    /// <summary>Encrypted sync blobs are base64 ASCII, so character length equals byte length.</summary>
    public Task<int> GetStoredBlobLengthAsync() => Db.ExecuteScalarAsync<int>(
        "SELECT COALESCE((SELECT LENGTH(Value) FROM Blob WHERE Key = ?), 0)", BlobBookmarks);

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
        var folderPaths = BuildPaths(all.Where(r => r.IsFolder).ToList(), rawTitles: true, includeSelf: true);
        var keys = new Dictionary<int, string>();
        foreach (var r in all)
        {
            keys[r.RowId] = r.IsFolder ? "f:" + folderPaths[r.RowId]
                : r.Url is { Length: > 0 } url ? "u:" + url + PinTitleSep + r.Title : "n:" + r.RowId;
        }

        return keys;
    }

    private static string UrlPart(string key) =>
        key.StartsWith("u:", StringComparison.Ordinal) && key.IndexOf(PinTitleSep, StringComparison.Ordinal) is int i and >= 0
            ? key[..i] : key;

    /// <summary>Rows for the given pin keys, in key order; one row per key (first copy).</summary>
    public async Task<(List<ItemRow> Rows, Dictionary<int, string> KeyByRow)> GetPinnedAsync(IReadOnlyList<string> pinKeys)
    {
        if (pinKeys.Count == 0)
        {
            return ([], []);
        }

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

    public async Task<Dictionary<int, string>> GetFolderPathsAsync() =>
        BuildPaths(await Db.QueryAsync<ItemRow>("SELECT RowId, ParentRowId, Title FROM Items").ConfigureAwait(false));

    /// <summary>Loads only requested rows and their shared ancestors, never the complete archive.</summary>
    public async Task<Dictionary<int, string>> GetFolderPathsAsync(IEnumerable<int> rowIds)
    {
        var ids = rowIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var all = new Dictionary<int, ItemRow>();
        foreach (var chunk in ids.Chunk(500))
        {
            var marks = string.Join(",", Enumerable.Repeat("?", chunk.Length));
            // UNION (rather than UNION ALL) bounds even corrupt ancestor cycles.
            var ancestors = await Db.QueryAsync<ItemRow>(
                $"WITH RECURSIVE ancestors AS (SELECT RowId, ParentRowId, Title FROM Items WHERE RowId IN ({marks}) " +
                "UNION SELECT i.RowId, i.ParentRowId, i.Title FROM Items i JOIN ancestors a ON i.RowId = a.ParentRowId) " +
                "SELECT * FROM ancestors", chunk.Cast<object>().ToArray()).ConfigureAwait(false);
            foreach (var row in ancestors)
            {
                all.TryAdd(row.RowId, row);
            }
        }

        var paths = BuildPaths(all.Values.ToList());
        return ids.Where(paths.ContainsKey).ToDictionary(id => id, id => paths[id]);
    }

    public async Task<List<ItemRow>> GetNodesAsync(IEnumerable<int> rowIds)
    {
        var result = new List<ItemRow>();
        foreach (var chunk in rowIds.Distinct().Chunk(500))
        {
            var marks = string.Join(",", Enumerable.Repeat("?", chunk.Length));
            result.AddRange(await Db.QueryAsync<ItemRow>($"SELECT * FROM Items WHERE RowId IN ({marks})",
                chunk.Cast<object>().ToArray()).ConfigureAwait(false));
        }

        return result;
    }

    private static Dictionary<int, string> BuildPaths(List<ItemRow> rows, bool rawTitles = false, bool includeSelf = false)
    {
        var byId = rows.ToDictionary(r => r.RowId);
        var fullPaths = new Dictionary<int, string>();
        var result = new Dictionary<int, string>();
        var hasParts = new Dictionary<int, bool>();
        var separator = rawTitles ? "\u001f" : " › ";
        foreach (var row in rows)
        {
            var start = includeSelf ? row
                : row.ParentRowId is int parentId && byId.TryGetValue(parentId, out var parentRow) ? parentRow : null;
            if (start is null)
            {
                result[row.RowId] = "";
                continue;
            }

            if (fullPaths.TryGetValue(start.RowId, out var existing))
            {
                result[row.RowId] = existing;
                continue;
            }

            var chain = new List<ItemRow>();
            var visiting = new HashSet<int>();
            var cur = start;
            while (!fullPaths.ContainsKey(cur.RowId) && visiting.Add(cur.RowId))
            {
                chain.Add(cur);
                if (cur.ParentRowId is not int parent || !byId.TryGetValue(parent, out cur))
                {
                    break;
                }
            }

            var path = cur is not null && fullPaths.TryGetValue(cur.RowId, out var cached) ? cached : "";
            var hasPart = cur is not null && hasParts.GetValueOrDefault(cur.RowId);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var item = chain[i];
                var title = rawTitles ? item.Title ?? "" : item.DisplayTitle;
                if (title is not null)
                {
                    path = hasPart ? path + separator + title : title;
                    hasPart = true;
                }

                fullPaths[item.RowId] = path;
                hasParts[item.RowId] = hasPart;
            }

            result[row.RowId] = fullPaths.GetValueOrDefault(start.RowId, "");
        }

        return result;
    }

    private static List<ItemRow> LoadSubtrees(SQLiteConnection tran, IEnumerable<int> roots)
    {
        var rows = new Dictionary<int, ItemRow>();
        foreach (var chunk in roots.Distinct().Chunk(500))
        {
            var marks = string.Join(",", Enumerable.Repeat("?", chunk.Length));
            foreach (var row in tran.Query<ItemRow>(
                $"WITH RECURSIVE subtree AS (SELECT * FROM Items WHERE RowId IN ({marks}) " +
                "UNION SELECT i.* FROM Items i JOIN subtree s ON i.ParentRowId = s.RowId) " +
                "SELECT * FROM subtree ORDER BY SortOrder", chunk.Cast<object>().ToArray()))
            {
                rows.TryAdd(row.RowId, row);
            }
        }

        return rows.Values.ToList();
    }

    private static void DeleteRows(SQLiteConnection tran, IEnumerable<int> ids)
    {
        foreach (var chunk in ids.Chunk(500))
        {
            var marks = string.Join(",", Enumerable.Repeat("?", chunk.Length));
            tran.Execute($"DELETE FROM Items WHERE RowId IN ({marks})", chunk.Cast<object>().ToArray());
        }
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
