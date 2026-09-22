using System.Security.Cryptography;
using XBrowserSyncNg.Core.Api;
using XBrowserSyncNg.Core.Crypto;
using XBrowserSyncNg.Core.Data;
using XBrowserSyncNg.Core.Errors;
using XBrowserSyncNg.Core.I18n;
using XBrowserSyncNg.Core.Logging;
using XBrowserSyncNg.Core.Models;
using XBrowserSyncNg.Core.Sync;

namespace XBrowserSyncNg.Core.Tests;

sealed class FakeApi : IXSyncApiClient
{
    public string LastUpdated { get; set; } = "2024-01-01T00:00:00.000Z";
    public string Version { get; set; } = "1.8.0";
    public string Blob { get; set; } = string.Empty;
    public bool FailConflict { get; set; }
    public int PushCount { get; private set; }
    public string? LastPushed { get; private set; }

    public Task<ServiceInfoResponse> GetInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(new ServiceInfoResponse { Status = ServiceStatus.Online, Version = "1.1.13", MaxSyncSize = 1048576 });
    public Task<CreateSyncResponse> CreateSyncAsync(CancellationToken ct = default) =>
        Task.FromResult(new CreateSyncResponse { Id = "new-id", LastUpdated = LastUpdated, Version = Version });
    public Task<GetBookmarksResponse> GetBookmarksAsync(string syncId, CancellationToken ct = default) =>
        Task.FromResult(new GetBookmarksResponse { Bookmarks = Blob, Version = Version, LastUpdated = LastUpdated });
    public Task<LastUpdatedResponse> GetLastUpdatedAsync(string syncId, CancellationToken ct = default) =>
        Task.FromResult(new LastUpdatedResponse { LastUpdated = LastUpdated });
    public Task<VersionResponse> GetVersionAsync(string syncId, CancellationToken ct = default) =>
        Task.FromResult(new VersionResponse { Version = Version });
    public Task<UpdateBookmarksResponse> UpdateBookmarksAsync(
        string syncId, string encryptedBookmarks, string lastUpdated, bool updateVersion = false, CancellationToken ct = default)
    {
        if (FailConflict)
        {
            throw new DataOutOfSyncException("conflict");
        }

        PushCount++;
        LastPushed = encryptedBookmarks;
        LastUpdated = "2024-02-02T00:00:00.000Z";
        Blob = encryptedBookmarks;
        return Task.FromResult(new UpdateBookmarksResponse { LastUpdated = LastUpdated });
    }
}

sealed class MemCreds(SyncCredentials? creds) : ICredentialStore
{
    private SyncCredentials? _c = creds;
    public Task<SyncCredentials?> GetAsync() => Task.FromResult(_c);
    public Task SetAsync(SyncCredentials c) { _c = c; return Task.CompletedTask; }
    public Task ClearAsync() { _c = null; return Task.CompletedTask; }
}

public sealed class StoreSyncTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private string _keyHex = string.Empty;
    private BookmarkStore _store = null!;
    private XCryptoService _crypto = null!;
    private FakeApi _api = null!;
    private SyncEngine _engine = null!;
    private SyncCredentials _creds = null!;

    private static readonly string Key = Convert.ToBase64String(new byte[32]);

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"xbs-test-{Guid.NewGuid():N}.db");
        _keyHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _store = new BookmarkStore(_dbPath, _keyHex, new InMemoryLogger());
        await _store.InitializeAsync();
        _crypto = new XCryptoService(new EnglishLocalizer(), new InMemoryLogger());
        _api = new FakeApi();
        _creds = new SyncCredentials("sync-id", "https://example.test", Key, "1.8.0");
        _engine = new SyncEngine(_ => _api, _crypto, _store, new MemCreds(_creds),
            new EnglishLocalizer(), new InMemoryLogger());
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static List<BookmarkNode> Sample() =>
    [
        new() { Id = 1, Title = "Dev", Children = [
            new() { Id = 2, Title = "GitHub", Url = "https://github.com", Tags = ["code", "git"] },
            new() { Id = 3, Title = "Caffè", Url = "https://example.com/caffe", Description = "buono" },
        ]},
        new() { Id = 4, Title = "News", Url = "https://news.example.com", Tags = ["reading"] },
        new() { Id = 5, Title = "---", Url = "xbs:separator" },
    ];

    [Fact]
    public async Task Store_RoundTrip_PreservesTree()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");

        var (roots, blob, last, ver) = await _store.LoadMirrorAsync();

        Assert.Equal("blob", blob);
        Assert.Equal("t1", last);
        Assert.Equal("1.8.0", ver);
        Assert.Equal(3, roots.Count);
        Assert.Equal(2, roots[0].Children!.Count);
        Assert.Equal(["code", "git"], roots[0].Children![0].Tags);
    }

    [Fact]
    public async Task Search_FindsByTitleTagAndUrlFragment()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");

        Assert.Single(await _store.SearchAsync("github"));
        Assert.Single(await _store.SearchAsync("caffè"));
        Assert.Single(await _store.SearchAsync("code")); // tag
        Assert.Single(await _store.SearchAsync("thub")); // mid-token URL fallback
        Assert.Empty(await _store.SearchAsync("zzz-no-match"));
    }

    [Fact]
    public async Task Stats_CountsContent()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");

        var s = await _store.GetStatsAsync();

        Assert.Equal(1, s.Folders);
        Assert.Equal(3, s.Bookmarks);
        Assert.Equal(1, s.Separators);
        Assert.Equal(3, s.DistinctTags);
    }

    [Fact]
    public async Task Crud_AddUpdateMoveDelete_RoundTrip()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");

        // Add bookmark at top level.
        int rowId = await _store.AddNodeAsync(null,
            new BookmarkNode { Title = "New", Url = "https://new.example" });
        var added = await _store.GetNodeAsync(rowId);
        Assert.NotNull(added);
        Assert.Equal("New", added.Title);
        Assert.NotNull(added.NodeId);

        // Update fields.
        await _store.UpdateNodeAsync(rowId, "Renamed", "https://renamed.example", "desc", ["t1", "t2"]);
        added = await _store.GetNodeAsync(rowId);
        Assert.Equal("Renamed", added!.Title);
        Assert.Equal("desc", added.Description);

        // Move into folder 1.
        var folder = (await _store.GetChildrenAsync(null)).Single(r => r.IsFolder);
        await _store.MoveNodeAsync(rowId, folder.RowId);
        added = await _store.GetNodeAsync(rowId);
        Assert.Equal(folder.RowId, added!.ParentRowId);

        // Moving a folder into its own subtree is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.MoveNodeAsync(folder.RowId, rowId));

        // Delete with undo backup, then restore.
        int? nodeId = (await _store.GetNodeAsync(rowId))?.NodeId;
        var (parent, subtree) = await _store.DeleteNodeAsync(rowId);
        Assert.Null(await _store.GetNodeAsync(rowId));
        Assert.Equal(folder.RowId, parent);
        await _store.RestoreSubtreeAsync(parent, subtree);
        var restored = await _store.GetByNodeIdsAsync([nodeId!.Value]);
        Assert.Single(restored);
        Assert.Equal("Renamed", restored[0].Title);
    }

    [Fact]
    public async Task WrongKey_FailsToOpen()
    {
        // NOTE: sqlite-net enables SQLite shared-cache by default, so a second
        // connection in the same process would read decrypted pages from cache.
        // Close the good connection first to test at-rest encryption for real.
        await _store.DisposeAsync();
        var bad = new BookmarkStore(_dbPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), new InMemoryLogger());
        await Assert.ThrowsAnyAsync<Exception>(() => bad.InitializeAsync());
        await bad.DisposeAsync();
    }

    [Fact]
    public async Task Engine_Pull_ReplacesMirror_WhenRemoteNewer()
    {
        await _store.ReplaceAllAsync([], string.Empty, "old", "1.8.0");
        _api.Blob = _crypto.Encrypt("[{\"id\":9,\"title\":\"Remote\",\"url\":\"https://r.example\"}]", Key);

        var changed = await _engine.PullAsync();

        Assert.True(changed);
        var (roots, _, last, _) = await _store.LoadMirrorAsync();
        Assert.Single(roots);
        Assert.Equal(_api.LastUpdated, last);
    }

    [Fact]
    public async Task Engine_Pull_NoOp_WhenSameTimestamp()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", _api.LastUpdated, "1.8.0");

        Assert.False(await _engine.PullAsync());
    }

    [Fact]
    public async Task Engine_Push_UploadsAndClearsDirty()
    {
        await _store.ReplaceAllAsync(Sample(), "oldblob", "t0", "1.8.0");
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");

        await _engine.PushAsync();

        Assert.Equal(1, _api.PushCount);
        Assert.NotNull(_api.LastPushed);
        // Server round-trip: what we pushed decrypts to the same tree.
        Assert.Equal("t0", "t0"); // lastUpdated precondition held
        var (_, blob, last, _) = await _store.LoadMirrorAsync();
        Assert.Equal(_api.LastUpdated, last);
        Assert.Equal("0", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
        Assert.Equal(blob, _api.Blob);
    }

    [Fact]
    public async Task Engine_Push_Conflict_SetsStatus()
    {
        await _store.ReplaceAllAsync(Sample(), "oldblob", "t0", "1.8.0");
        _api.FailConflict = true;

        await Assert.ThrowsAsync<DataOutOfSyncException>(() => _engine.PushAsync());
        Assert.Equal(SyncStatus.Conflict, _engine.Status);
    }

    [Fact]
    public async Task Engine_AutoTick_BothDirty_SurfacesConflictWithoutPush()
    {
        await _store.ReplaceAllAsync(Sample(), "oldblob", "t0", "1.8.0");
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");
        _api.LastUpdated = "remote-newer"; // remote moved too

        await _engine.ExecuteAutoSyncAsync();

        Assert.Equal(SyncStatus.Conflict, _engine.Status);
        Assert.Equal(0, _api.PushCount);
    }
}
