using Keepmark.Core.Data;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
using SQLite;

namespace Keepmark.Core.Tests;

public sealed class StorePerformanceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"keepmark-perf-{Guid.NewGuid():N}.db");
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private BookmarkStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new BookmarkStore(_path, Key, new InMemoryLogger());
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        File.Delete(_path);
    }

    [Fact]
    public async Task RuntimeUsesMaintainedSqlcipherAndSqlite()
    {
        var db = new SQLiteAsyncConnection(_path);
        Assert.StartsWith("4.19.0", await db.ExecuteScalarAsync<string>("PRAGMA cipher_version"));
        Assert.Equal("3.53.4", await db.ExecuteScalarAsync<string>("SELECT sqlite_version()"));
    }

    [Fact]
    public async Task NativeSqlcipher419_OpensAndUpdatesEncryptedDatabaseFrom210()
    {
        await _store.DisposeAsync();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "sqlcipher-2.1.10.db"), _path, overwrite: true);
        _store = new BookmarkStore(_path, Key, new InMemoryLogger());
        await _store.InitializeAsync();
        var mirror = await _store.LoadMirrorAsync();
        Assert.Equal("legacy-blob", mirror.Blob);
        Assert.Equal("7", await _store.GetMetaAsync(BookmarkStore.MetaRevision));
        Assert.Equal("Legacy folder", mirror.Roots.Single().Title);
        Assert.Equal("https://legacy.example.test", mirror.Roots.Single().Children!.Single().Url);
        Assert.Equal(2, (await _store.SearchAsync("Legacy")).Count);
        var id = await _store.AddNodeByParentNodeIdAsync(1,
            new BookmarkNode { Title = "Upgraded bookmark", Url = "https://updated.example.test" });
        Assert.True(id > 0);
        await _store.DisposeAsync();
        _store = new BookmarkStore(_path, Key, new InMemoryLogger());
        await _store.InitializeAsync();
        Assert.Equal(2, (await _store.LoadMirrorAsync()).Roots.Single().Children!.Count);
        Assert.Single(await _store.SearchAsync("Upgraded"));
        // The fixture and modified copy remain encrypted; test an independent
        // path so sqlite-net's shared connection pool cannot reuse the keyed connection.
        var plainPath = _path + ".no-key";
        File.Copy(_path, plainPath);
        var plain = new SQLiteAsyncConnection(plainPath);
        try
        {
            await Assert.ThrowsAsync<SQLiteException>(() => plain.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items"));
        }
        finally
        {
            await plain.CloseAsync();
            File.Delete(plainPath);
        }
    }

    [Fact]
    public async Task TargetedPathsAndCounts_MatchTreeAfterMoveAndRename()
    {
        await _store.ReplaceAllAsync([
            new() { Id = 1, Title = "Root", Children = [
                new() { Id = 2, Title = "Nested", Children = [
                    new() { Id = 3, Title = "Needle", Url = "https://needle.test" },
                    new() { Id = 4, Url = Core.XbsConstants.SeparatorUrl }] },
                new() { Id = 5, Title = "Empty", Children = [] },
                new() { Id = 6, Title = "Other", Url = "https://other.test" }] }
        ], "blob", "t1", "1.8.0");
        var rows = (await _store.GetByNodeIdsAsync([1, 2, 3, 4, 5, 6])).ToDictionary(r => r.NodeId!.Value);
        var counts = await _store.GetFolderBookmarkCountsAsync();
        Assert.Equal(2, counts[rows[1].RowId]);
        Assert.Equal(1, counts[rows[2].RowId]);
        Assert.Equal(0, counts[rows[5].RowId]);
        var targeted = await _store.GetFolderPathsAsync([rows[3].RowId, rows[6].RowId]);
        Assert.Equal(2, targeted.Count);
        Assert.Equal("Root › Nested", targeted[rows[3].RowId]);
        Assert.Equal("Root", targeted[rows[6].RowId]);
        Assert.Equal(2, (await _store.GetStatsAsync()).MaxDepth);
        Assert.Equal("Root › Nested", (await _store.SearchAsync("Needle")).Single().FolderPath);
        var version = _store.ContentVersion;
        await _store.MoveNodeByNodeIdAsync(3, 5);
        await _store.UpdateNodeByNodeIdAsync(5, "Renamed", null, null, null);
        Assert.True(_store.ContentVersion > version);
        Assert.Equal("Root › Renamed", (await _store.SearchAsync("Needle")).Single().FolderPath);
        counts = await _store.GetFolderBookmarkCountsAsync();
        Assert.Equal(0, counts[rows[2].RowId]);
        Assert.Equal(1, counts[rows[5].RowId]);
        Assert.Equal(2, (await _store.GetNodesAsync([rows[1].RowId, rows[3].RowId, rows[3].RowId])).Count);
    }

    [Fact]
    public async Task UploadAcknowledgement_PreservesRowIdsNormalizesAndClearsLegacyMeta()
    {
        var longDescription = new string('a', Core.XbsConstants.DescriptionMaxLength + 10) + "trailingneedle";
        await _store.ReplaceAllAsync([new() { Id = 1, Title = "Folder", Children = [
            new() { Id = 2, Title = "Bookmark", Url = "https://example.test", Description = longDescription }]
        }], "old", "t1", "1.8.0", 4, "oldhash");
        var rowsBefore = await _store.GetByNodeIdsAsync([1, 2]);
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");
        var version = _store.ContentVersion;
        Assert.True(await _store.AcknowledgePushAsync("new", "t2", "1.8.0", 5, "newhash"));
        var rowsAfter = await _store.GetByNodeIdsAsync([1, 2]);
        Assert.Equal(rowsBefore.Select(r => r.RowId), rowsAfter.Select(r => r.RowId));
        Assert.Equal(Core.XbsConstants.DescriptionMaxLength, rowsAfter.Single(r => r.NodeId == 2).Description!.Length);
        Assert.Empty(await _store.SearchAsync("trailingneedle"));
        Assert.True(_store.ContentVersion > version);
        version = _store.ContentVersion;
        Assert.False(await _store.AcknowledgePushAsync("newer", "t3", "1.8.0"));
        Assert.Equal(version, _store.ContentVersion);
        Assert.Equal("0", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
        Assert.Null(await _store.GetMetaAsync(BookmarkStore.MetaRevision));
        Assert.Null(await _store.GetMetaAsync(BookmarkStore.MetaHash));
        Assert.Equal("newer", (await _store.LoadMirrorAsync()).Blob);
        Assert.Equal(5, await _store.GetStoredBlobLengthAsync());
    }

    [Fact]
    public async Task IndexesCoverLookupAndNavigation_AndDuplicateCountMatchesRows()
    {
        await _store.ReplaceAllAsync([
            new() { Id = 1, Url = "https://dup.test" }, new() { Id = 2, Url = "https://dup.test" },
            new() { Id = 3, Url = Core.XbsConstants.SeparatorUrl }, new() { Id = 4, Url = Core.XbsConstants.SeparatorUrl }
        ], "b", "t", "v");
        Assert.Equal((await _store.GetDuplicateUrlsAsync()).Sum(g => g.Rows.Count - 1), await _store.CountDuplicateBookmarksAsync());
        Assert.Equal(1, await _store.CountDuplicateBookmarksAsync());
        var db = new SQLiteAsyncConnection(_path);
        await db.ExecuteScalarAsync<int>($"PRAGMA key = \"x'{Key}'\"");
        var plans = await db.QueryAsync<QueryPlan>("EXPLAIN QUERY PLAN SELECT * FROM Items WHERE ParentRowId IS ? ORDER BY SortOrder", 1);
        Assert.Contains(plans, p => p.Detail.Contains("IX_Items_Parent_Order"));
        plans = await db.QueryAsync<QueryPlan>("EXPLAIN QUERY PLAN SELECT * FROM Items WHERE NodeId = ?", 1);
        Assert.Contains(plans, p => p.Detail.Contains("IX_Items_NodeId"));
        // Shared sqlite-net pool is closed by the store fixture.
        Assert.Empty((await _store.GetPinnedAsync([])).Rows);
    }

    public sealed class QueryPlan
    {
        public string Detail { get; set; } = "";
    }

    [Fact]
    public async Task DeleteUndo_EmptyFolderRemainsFolder()
    {
        await _store.ReplaceAllAsync([new() { Id = 1, Title = "Empty", Children = [] }], "b", "t", "v");
        var (parent, subtree) = await _store.DeleteNodeByNodeIdAsync(1);
        Assert.NotNull(subtree.Single().Children);
        await _store.RestoreSubtreeByNodeIdAsync(parent, subtree);
        Assert.True((await _store.GetByNodeIdsAsync([1])).Single().IsFolder);
    }

    [Fact]
    public async Task DeepTreeAggregationAndDeletion_PreserveUnrelatedRows()
    {
        var root = new BookmarkNode { Id = 1, Title = "0", Children = [] };
        var cursor = root;
        for (int i = 2; i <= 300; i++)
        {
            var child = new BookmarkNode { Id = i, Title = i.ToString(), Children = [] };
            cursor.Children!.Add(child);
            cursor = child;
        }
        cursor.Children!.Add(new() { Id = 301, Title = "Leaf", Url = "https://leaf.test" });
        await _store.ReplaceAllAsync([root, new() { Id = 400, Title = "Survivor", Url = "https://stay.test" }], "b", "t", "v");
        var counts = await _store.GetFolderBookmarkCountsAsync();
        Assert.Equal(300, counts.Count);
        Assert.All(counts.Values, n => Assert.Equal(1, n));
        Assert.Equal(300, (await _store.GetStatsAsync()).MaxDepth);
        var survivor = (await _store.GetByNodeIdsAsync([400])).Single();
        Assert.Equal(301, await _store.DeleteNodesByNodeIdsAsync([1, 2]));
        Assert.Equal(survivor.RowId, (await _store.GetByNodeIdsAsync([400])).Single().RowId);
        Assert.Empty(await _store.SearchAsync("Leaf"));
    }
}
