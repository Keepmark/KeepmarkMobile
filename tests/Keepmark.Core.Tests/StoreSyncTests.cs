using System.Security.Cryptography;
using Keepmark.Core.Api;
using Keepmark.Core.Crypto;
using Keepmark.Core.Data;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
using Keepmark.Core.Sync;

namespace Keepmark.Core.Tests;

sealed class FakeApi : IXSyncApiClient
{
    public string LastUpdated { get; set; } = "2024-01-01T00:00:00.000Z";
    public string Version { get; set; } = "1.8.0";
    public string Blob { get; set; } = string.Empty;
    public bool FailConflict { get; set; }
    /// <summary>Real-server semantics: a timestamp base that differs from the
    /// current lastUpdated loses with 409 (Node + KeepmarkApi behave so).</summary>
    public bool EnforceTimestampBase { get; set; }
    /// <summary>Number of upcoming PUTs that lose with 409 because another
    /// device "pushed" in between (remote moves on each loss).</summary>
    public int ConflictsBeforeSuccess { get; set; }
    public bool Offline { get; set; }
    public int PushCount { get; private set; }
    public string? LastPushed { get; private set; }
    public bool V12 { get; set; }
    public long Revision { get; set; } = 7;
    public string Hash => Crypto.BlobHash.Of(Blob);
    public long? LastBaseRevision { get; private set; }
    public int ConditionalHits { get; private set; }
    /// <summary>Applies the write, then throws conflict (ambiguous-timeout simulation).</summary>
    public bool ApplyThenConflict { get; set; }
    /// <summary>When set, a conflicting write replaces the remote blob with this instead.</summary>
    public string? ConflictSwapBlob { get; set; }

    public Task<ServiceInfoResponse> GetInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(new ServiceInfoResponse
        {
            Status = ServiceStatus.Online,
            Version = "1.1.13",
            MaxSyncSize = 1048576,
            Capabilities = V12 ? new ApiCapabilities { Revision = true, ConditionalGet = true, Delete = true } : null,
        });
    public Task<ServiceInfoResponse> GetInfoV12Async(CancellationToken ct = default) => GetInfoAsync(ct);
    public Task<CreateSyncResponse> CreateSyncAsync(CancellationToken ct = default) =>
        Task.FromResult(new CreateSyncResponse { Id = "new-id", LastUpdated = LastUpdated, Version = Version });
    public Task<GetBookmarksResponse> GetBookmarksAsync(string syncId, CancellationToken ct = default) =>
        Task.FromResult(new GetBookmarksResponse
        {
            Bookmarks = Blob,
            Version = Version,
            LastUpdated = LastUpdated,
            Meta = V12 ? new SyncMeta { Revision = Revision, Hash = Hash } : null,
        });
    public Task<GetBookmarksResponse> GetBookmarksV12Async(string syncId, CancellationToken ct = default) =>
        GetBookmarksAsync(syncId, ct);
    public Task<GetBookmarksResponse?> GetBookmarksIfChangedAsync(string syncId, string knownHash, CancellationToken ct = default)
    {
        ConditionalHits++;
        // Match when the known hash equals the hash of the current remote blob.
        var current = Crypto.BlobHash.Of(Blob);
        return Task.FromResult<GetBookmarksResponse?>(string.Equals(knownHash, current, StringComparison.Ordinal) ? null : new GetBookmarksResponse
        {
            Bookmarks = Blob,
            Version = Version,
            LastUpdated = LastUpdated,
            Meta = new SyncMeta { Revision = Revision, Hash = current },
        });
    }
    public Task<LastUpdatedResponse> GetLastUpdatedAsync(string syncId, CancellationToken ct = default) =>
        Task.FromResult(new LastUpdatedResponse { LastUpdated = LastUpdated });
    public Task<VersionResponse> GetVersionAsync(string syncId, CancellationToken ct = default) =>
        Task.FromResult(new VersionResponse { Version = Version });
    public Task<UpdateBookmarksResponse> UpdateBookmarksAsync(
        string syncId, string encryptedBookmarks, string lastUpdated, bool updateVersion = false, CancellationToken ct = default)
    {
        if (Offline)
        {
            throw new ServiceOfflineException("offline");
        }

        if ((FailConflict && !ApplyThenConflict) || (EnforceTimestampBase && lastUpdated.Length > 0 && lastUpdated != LastUpdated))
        {
            throw new DataOutOfSyncException("conflict");
        }

        if (ConflictsBeforeSuccess > 0)
        {
            ConflictsBeforeSuccess--;
            LastUpdated = $"2024-05-0{ConflictsBeforeSuccess + 1}T00:00:00.000Z";
            Revision++;
            throw new DataOutOfSyncException("conflict");
        }

        PushCount++;
        LastPushed = encryptedBookmarks;
        LastUpdated = $"2024-02-02T00:00:{PushCount:00}.000Z";
        Blob = ConflictSwapBlob ?? encryptedBookmarks;
        if (ApplyThenConflict)
        {
            throw new DataOutOfSyncException("conflict");
        }

        return Task.FromResult(new UpdateBookmarksResponse { LastUpdated = LastUpdated });
    }

    public Task<UpdateBookmarksResponse> UpdateBookmarksV12Async(
        string syncId, string encryptedBookmarks, long baseRevision, bool updateVersion = false, CancellationToken ct = default)
    {
        LastBaseRevision = baseRevision;
        if ((FailConflict && !ApplyThenConflict) || baseRevision != Revision)
        {
            throw new DataOutOfSyncException("conflict");
        }

        if (ConflictsBeforeSuccess > 0)
        {
            ConflictsBeforeSuccess--;
            LastUpdated = $"2024-05-0{ConflictsBeforeSuccess + 1}T00:00:00.000Z";
            Revision++;
            throw new DataOutOfSyncException("conflict");
        }

        PushCount++;
        LastPushed = encryptedBookmarks;
        LastUpdated = $"2024-02-02T00:00:{PushCount:00}.000Z";
        Blob = ConflictSwapBlob ?? encryptedBookmarks;
        if (ApplyThenConflict)
        {
            throw new DataOutOfSyncException("conflict");
        }

        Revision++;
        return Task.FromResult(new UpdateBookmarksResponse
        {
            LastUpdated = LastUpdated,
            Meta = new SyncMeta { Revision = Revision, Hash = Crypto.BlobHash.Of(Blob) },
        });
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
        _crypto = new XCryptoService(new XbsLocalizer(), new InMemoryLogger());
        _api = new FakeApi();
        _creds = new SyncCredentials("sync-id", "https://example.test", Key, "1.8.0");
        _engine = new SyncEngine(_ => _api, _crypto, _store, new MemCreds(_creds),
            new XbsLocalizer(), new InMemoryLogger());
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
    public async Task RecentBookmarks_NewestNodeIdFirst_BookmarksOnly()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        var dev = (await _store.GetByNodeIdsAsync([1])).Single();
        await _store.AddNodeAsync(dev.RowId, new BookmarkNode { Title = "Added", Url = "https://added.example.com" });

        var recent = await _store.GetRecentBookmarksAsync(10);

        // Folder "Dev" and the separator are skipped; the new item got MAX+1.
        Assert.Equal(["Added", "News", "Caffè", "GitHub"], recent.Select(r => r.Title));
        Assert.Equal(["Added", "News"], (await _store.GetRecentBookmarksAsync(2)).Select(r => r.Title));
    }

    [Fact]
    public async Task DateAdded_RoundTrips_AndNewNodesAreStamped()
    {
        List<BookmarkNode> tree =
        [
            new() { Id = 1, Title = ContainerNames.Other, Children = [
                new() { Id = 2, Title = "Dated", Url = "https://dated.example.com", DateAdded = 1_700_000_000_000 },
                new() { Id = 3, Title = "Legacy", Url = "https://legacy.example.com" },
            ]},
        ];
        await _store.ReplaceAllAsync(tree, "blob", "t1", "1.8.0");
        var other = (await _store.GetByNodeIdsAsync([1])).Single();
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.AddNodeAsync(other.RowId, new BookmarkNode { Title = "New", Url = "https://new.example.com" });
        await _store.AddNodeAsync(other.RowId, new BookmarkNode { Title = "Copied", Url = "https://c.example.com", DateAdded = 42 });

        var (roots, _, _, _) = await _store.LoadMirrorAsync();
        var kids = roots[0].Children!;
        Assert.Equal(1_700_000_000_000, kids[0].DateAdded);
        Assert.Null(kids[1].DateAdded);
        Assert.InRange(kids[2].DateAdded!.Value, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(42, kids[3].DateAdded);

        // Wire: camelCase number when set, omitted when null.
        var json = System.Text.Json.JsonSerializer.Serialize(kids.Take(2));
        Assert.Contains("\"dateAdded\":1700000000000", json);
        Assert.Equal(1, json.Split("dateAdded").Length - 1);
    }

    [Fact]
    public async Task Containers_ShowLocalizedNames_ButKeepSyncedTitles()
    {
        List<BookmarkNode> tree =
        [
            new() { Id = 1, Title = ContainerNames.Menu, Children = [
                new() { Id = 2, Title = ContainerNames.Menu, Children = [
                    new() { Id = 3, Title = "Deep", Url = "https://deep.example.com" },
                ]},
            ]},
            new() { Id = 4, Title = ContainerNames.Other, Children = [
                new() { Id = 5, Title = "Docs", Url = "https://docs.example.com" },
            ]},
            new() { Id = 6, Title = ContainerNames.Toolbar, Children = [] },
        ];
        await _store.ReplaceAllAsync(tree, "blob", "t1", "1.8.0");

        // Synced titles are untouched: the mirror pushed to the service keeps "[xbs] …".
        var (roots, _, _, _) = await _store.LoadMirrorAsync();
        Assert.Equal([ContainerNames.Menu, ContainerNames.Other, ContainerNames.Toolbar], roots.Select(r => r.Title));
        Assert.Equal(ContainerNames.Menu, roots[0].Children![0].Title);

        var rows = await _store.GetChildrenAsync(null);
        Assert.Equal(["Menu", "Other", "Toolbar"], rows.Select(r => r.DisplayTitle));
        Assert.Equal([ContainerNames.Menu, ContainerNames.Other, ContainerNames.Toolbar], rows.Select(r => r.Title));

        // Only top-level containers are renamed: a nested folder titled "[xbs] Menu" stays as is.
        var paths = await _store.GetFolderPathsAsync();
        var deep = (await _store.SearchAsync("deep")).Single();
        Assert.Equal($"Menu / {ContainerNames.Menu}", paths[deep.RowId]);
        Assert.Equal("Other", (await _store.SearchAsync("docs")).Single().FolderPath);
    }

    [Fact]
    public async Task PinKeys_SurviveFullReplaceWithRenumberedIds()
    {
        static List<BookmarkNode> Tree(int b) =>
        [
            new() { Id = b + 1, Title = ContainerNames.Toolbar, Children = [
                new() { Id = b + 2, Title = "Wiki", Url = "https://en.wikipedia.org/" },
                new() { Id = b + 3, Title = "Daily", Children = [
                    new() { Id = b + 4, Title = "HN", Url = "https://news.ycombinator.com/" },
                ]},
            ]},
            new() { Id = b + 5, Title = ContainerNames.Other, Children = [
                new() { Id = b + 6, Title = "Daily", Children = [] },
            ]},
        ];

        await _store.ReplaceAllAsync(Tree(0), "blob", "t1", "1.8.0");
        var keys = await _store.GetPinKeysAsync();
        var wiki = (await _store.SearchAsync("wiki")).Single(h => !h.IsFolder);
        var daily = (await _store.GetByNodeIdsAsync([3])).Single();
        List<string> pins = [keys[wiki.RowId], keys[daily.RowId]];

        // Another client rewrites the sync: same tree, every NodeId shifted.
        await _store.ReplaceAllAsync(Tree(100), "blob", "t2", "1.8.0");
        var (rows, keyByRow) = await _store.GetPinnedAsync(pins);

        Assert.Equal(["Wiki", "Daily"], rows.Select(r => r.Title));
        Assert.Equal([102, 103], rows.Select(r => r.NodeId));
        // Duplicate URL: the pinned copy wins; a title edit still keeps the pin.
        static List<BookmarkNode> Dup(string title) =>
        [
            new() { Id = 1, Title = ContainerNames.Other, Children = [new() { Id = 2, Title = "MDN", Url = "https://developer.mozilla.org/" }] },
            new() { Id = 3, Title = ContainerNames.Toolbar, Children = [new() { Id = 4, Title = title, Url = "https://developer.mozilla.org/" }] },
        ];
        await _store.ReplaceAllAsync(Dup("MDN Web Docs"), "blob", "t3", "1.8.0");
        var dupKeys = await _store.GetPinKeysAsync();
        var toolbarCopy = (await _store.GetByNodeIdsAsync([4])).Single();
        List<string> dupPins = [dupKeys[toolbarCopy.RowId]];
        Assert.Equal(["MDN Web Docs"], (await _store.GetPinnedAsync(dupPins)).Rows.Select(r => r.Title));
        await _store.ReplaceAllAsync(Dup("MDN docs"), "blob", "t4", "1.8.0");
        var (renamed, renamedKeys) = await _store.GetPinnedAsync(dupPins);
        Assert.Single(renamed);
        Assert.Equal(dupPins[0], renamedKeys[renamed[0].RowId]);
        await _store.ReplaceAllAsync(Tree(100), "blob", "t5", "1.8.0");
        (_, keyByRow) = await _store.GetPinnedAsync(pins);

        // Same-named folder elsewhere is a different pin.
        var otherDaily = (await _store.GetByNodeIdsAsync([106])).Single();
        Assert.DoesNotContain(keyByRow[otherDaily.RowId], pins);
    }

    [Fact]
    public void ContainerNames_LeaveOtherTitlesAlone()
    {
        Assert.Equal("Menu", ContainerNames.Display(ContainerNames.Menu, isTopLevel: true));
        Assert.Equal(ContainerNames.Menu, ContainerNames.Display(ContainerNames.Menu, isTopLevel: false));
        Assert.Equal("[xbs] Mobile", ContainerNames.Display("[xbs] Mobile", isTopLevel: true));
        Assert.Equal("Work", ContainerNames.Display("Work", isTopLevel: true));
        Assert.Null(ContainerNames.Display(null, isTopLevel: true));
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
    public async Task FolderBookmarkCounts_CountThroughSubfoldersAndSkipSeparators()
    {
        List<BookmarkNode> tree =
        [
            new() { Id = 1, Title = "Top", Children = [
                new() { Id = 2, Title = "A", Url = "https://a.example" },
                new() { Id = 3, Title = "---", Url = "xbs:separator" },
                new() { Id = 4, Title = "Sub", Children = [
                    new() { Id = 5, Title = "B", Url = "https://b.example" },
                    new() { Id = 6, Title = "C", Url = "https://c.example" },
                ]},
                new() { Id = 7, Title = "Empty", Children = [] },
            ]},
            new() { Id = 8, Title = "Loose", Url = "https://loose.example" },
        ];
        await _store.ReplaceAllAsync(tree, "blob", "t1", "1.8.0");

        var counts = await _store.GetFolderBookmarkCountsAsync();
        var folders = (await _store.GetAllFoldersAsync()).ToDictionary(f => f.Title!, f => f.RowId);

        Assert.Equal(3, counts.Count);
        Assert.Equal(3, counts[folders["Top"]]);
        Assert.Equal(2, counts[folders["Sub"]]);
        Assert.Equal(0, counts[folders["Empty"]]);
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
    public async Task Mutations_MarkMirrorDirty_Atomically()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        Assert.Equal("0", await _store.GetMetaAsync(BookmarkStore.MetaDirty));

        int rowId = await _store.AddNodeAsync(null,
            new BookmarkNode { Title = "New", Url = "https://new.example" });
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));

        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        int row2 = await _store.AddNodeAsync(null,
            new BookmarkNode { Title = "New", Url = "https://new.example" });
        await _store.UpdateNodeAsync(row2, "Renamed", "https://renamed.example", "desc", ["t1"]);
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));

        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        int row3 = await _store.AddNodeAsync(null,
            new BookmarkNode { Title = "New", Url = "https://new.example" });
        var folder = (await _store.GetChildrenAsync(null)).Single(r => r.IsFolder);
        await _store.MoveNodeAsync(row3, folder.RowId);
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));

        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        await _store.RestoreSubtreeAsync(null, [new BookmarkNode { Title = "U", Url = "https://u.example" }]);
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));

        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        int row4 = await _store.AddNodeAsync(null,
            new BookmarkNode { Title = "New", Url = "https://new.example" });
        await _store.DeleteNodeAsync(row4);
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));

        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        int row5 = await _store.AddNodeAsync(null,
            new BookmarkNode { Title = "New", Url = "https://new.example" });
        var nodeId = (await _store.GetNodeAsync(row5))?.NodeId;
        await _store.DeleteNodesByNodeIdsAsync([nodeId!.Value]);
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
    }

    [Fact]
    public async Task FindByNormalizedUrl_MatchesVariants_SkipsOthers()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");

        // Host case, trailing slash, fragment and tracking params are ignored.
        var hit = Assert.Single(await _store.FindByNormalizedUrlAsync("https://GitHub.com/#top"));
        Assert.Equal("GitHub", hit.Title);
        Assert.Single(await _store.FindByNormalizedUrlAsync("https://example.com/caffe/?utm_source=x"));

        Assert.Empty(await _store.FindByNormalizedUrlAsync("https://example.com/other"));
        Assert.Empty(await _store.FindByNormalizedUrlAsync("   "));
        // Separators never count as a saved link.
        Assert.Empty(await _store.FindByNormalizedUrlAsync("xbs:separator"));

        await _store.AddNodeAsync(null, new BookmarkNode { Title = "Copy", Url = "https://github.com/" });
        Assert.Equal(2, (await _store.FindByNormalizedUrlAsync("https://github.com")).Count);
    }

    [Fact]
    public async Task AddFolderWithChild_AssignsDistinctNodeIds()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        var parent = (await _store.GetChildrenAsync(null)).Single(r => r.IsFolder);

        // New folder + its first bookmark in one insert (picker draft folder).
        int folderRow = await _store.AddNodeByParentNodeIdAsync(parent.NodeId, new BookmarkNode
        {
            Title = "Draft",
            Children = [new BookmarkNode { Title = "Inside", Url = "https://inside.example" }]
        });

        var folder = await _store.GetNodeAsync(folderRow);
        Assert.NotNull(folder);
        Assert.True(folder!.IsFolder);
        var kids = await _store.GetChildrenAsync(folderRow);
        var child = Assert.Single(kids);
        Assert.Equal("Inside", child.Title);
        Assert.NotNull(folder.NodeId);
        Assert.NotNull(child.NodeId);
        Assert.NotEqual(folder.NodeId, child.NodeId);
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
    }

    [Fact]
    public async Task AddFolderWithChild_ReachesPushMirror_WithFreshIds()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        var before = (await _store.GetByNodeIdsAsync([1, 2, 3, 4, 5])).Select(r => r.NodeId).ToHashSet();

        await _store.AddNodeByParentNodeIdAsync(null, new BookmarkNode
        {
            Title = "Draft",
            Children = [new BookmarkNode { Title = "Inside", Url = "https://inside.example" }]
        });

        // The mirror is what the next push serializes: folder + child, both with
        // ids that collide with nothing already in the tree.
        var (roots, _, _, _) = await _store.LoadMirrorAsync();
        var draft = Assert.Single(roots, n => n.Title == "Draft");
        var child = Assert.Single(draft.Children!);
        Assert.Equal("https://inside.example", child.Url);
        Assert.NotNull(draft.Id);
        Assert.NotNull(child.Id);
        Assert.DoesNotContain(draft.Id, before);
        Assert.DoesNotContain(child.Id, before);
        Assert.NotEqual(draft.Id, child.Id);
    }

    [Fact]
    public async Task FindByNormalizedUrl_HostPrefilter_IsCaseInsensitive_AndToleratesBadInput()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        await _store.AddNodeAsync(null, new BookmarkNode { Title = "Shouty", Url = "HTTPS://SHOUT.EXAMPLE.COM/Path" });

        // Stored upper-case host still passes the SQL host pre-filter.
        Assert.Single(await _store.FindByNormalizedUrlAsync("https://shout.example.com/Path"));
        // Path case is significant: a different page is not a duplicate.
        Assert.Empty(await _store.FindByNormalizedUrlAsync("https://shout.example.com/path"));
        // Scheme-less / unparsable input takes the full scan and simply finds nothing.
        Assert.Empty(await _store.FindByNormalizedUrlAsync("github.com"));
        Assert.Empty(await _store.FindByNormalizedUrlAsync("not a url"));
    }

    [Fact]
    public async Task NodeIdMutations_SurviveReplaceAll_Rekey()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        var target = (await _store.GetByNodeIdsAsync([4])).Single();
        int nodeId = target.NodeId!.Value;

        // A push/pull rekeys every RowId; the NodeId stays valid.
        await _store.ReplaceAllAsync(Sample(), "blob", "t2", "1.8.0");

        await _store.UpdateNodeByNodeIdAsync(nodeId, "Renamed", target.Url, null, null);
        var updated = (await _store.GetByNodeIdsAsync([nodeId])).Single();
        Assert.Equal("Renamed", updated.Title);
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));

        var folder = (await _store.GetChildrenAsync(null)).Single(r => r.IsFolder);
        await _store.MoveNodeByNodeIdAsync(nodeId, folder.NodeId);
        var moved = (await _store.GetByNodeIdsAsync([nodeId])).Single();
        var folderRow = (await _store.GetByNodeIdsAsync([folder.NodeId!.Value])).Single();
        Assert.Equal(folderRow.RowId, moved.ParentRowId);

        var (parentNode, subtree) = await _store.DeleteNodeByNodeIdAsync(nodeId);
        Assert.Empty(await _store.GetByNodeIdsAsync([nodeId]));
        Assert.Equal(folder.NodeId, parentNode);
        await _store.RestoreSubtreeByNodeIdAsync(parentNode, subtree);
        Assert.Single(await _store.GetByNodeIdsAsync([nodeId]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.UpdateNodeByNodeIdAsync(999_999, "x", null, null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.DeleteNodeByNodeIdAsync(999_999));
    }

    [Fact]
    public async Task Engine_DeleteByNodeId_MarksDirtyAndPushesOnce()
    {
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");
        var target = (await _store.GetByNodeIdsAsync([2])).Single();
        await _store.ReplaceAllAsync(Sample(), "blob", "t1", "1.8.0");

        var (parentNode, _) = await _engine.DeleteBookmarkByNodeIdAsync(target.NodeId!.Value);

        Assert.NotNull(parentNode);
        Assert.Empty(await _store.GetByNodeIdsAsync([target.NodeId.Value]));
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
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
    public async Task Engine_Pull_RefusesWhenDirty_PreservesLocalEdits()
    {
        await _store.ReplaceAllAsync(Sample(), "oldblob", "t0", "1.8.0");
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");
        _api.Blob = _crypto.Encrypt("[{\"id\":9,\"title\":\"Remote\",\"url\":\"https://r.example\"}]", Key);

        await Assert.ThrowsAsync<DataOutOfSyncException>(() => _engine.PullAsync());
        Assert.Equal(SyncStatus.Conflict, _engine.Status);
        // Local tree untouched.
        var (roots, _, _, _) = await _store.LoadMirrorAsync();
        Assert.Equal(3, roots.Count);
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
    public async Task Engine_Push_V12_UsesRevisionBase_AndPersistsMeta()
    {
        _api.V12 = true;
        await _store.ReplaceAllAsync(Sample(), "oldblob", "t0", "1.8.0", revision: 7, hash: "hash-7");
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");

        await _engine.PushAsync();

        Assert.Equal(7, _api.LastBaseRevision);
        Assert.Equal("8", await _store.GetMetaAsync(BookmarkStore.MetaRevision));
        Assert.Equal(Crypto.BlobHash.Of(_api.LastPushed!), await _store.GetMetaAsync(BookmarkStore.MetaHash));
    }

    [Fact]
    public async Task Engine_Push_V12_StaleRevision_SurfacesConflict()
    {
        _api.V12 = true;
        _api.Revision = 9; // server moved on
        await _store.ReplaceAllAsync(Sample(), "oldblob", "t0", "1.8.0", revision: 7, hash: "hash-7");
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");

        await Assert.ThrowsAsync<DataOutOfSyncException>(() => _engine.PushAsync());
        Assert.Equal(SyncStatus.Conflict, _engine.Status);
    }

    [Fact]
    public async Task Engine_Push_LegacyServer_NeverSendsRevision()
    {
        _api.V12 = false;
        await _store.ReplaceAllAsync(Sample(), "oldblob", "t0", "1.8.0", revision: 7, hash: "hash-7");
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");

        await _engine.PushAsync();

        Assert.Null(_api.LastBaseRevision);
    }

    [Fact]
    public async Task Engine_Pull_V12_PersistsMeta_AndSkipsDownloadOn304()
    {
        _api.V12 = true;
        await _store.ReplaceAllAsync([], string.Empty, "old", "1.8.0");
        _api.Blob = _crypto.Encrypt("[{\"id\":9,\"title\":\"Remote\",\"url\":\"https://r.example\"}]", Key);

        Assert.True(await _engine.PullAsync());
        Assert.Equal("7", await _store.GetMetaAsync(BookmarkStore.MetaRevision));

        // Server metadata touched but content identical → 304, no download.
        _api.LastUpdated = "2024-03-03T00:00:00.000Z";
        Assert.False(await _engine.PullAsync());
        Assert.Equal(1, _api.ConditionalHits);
    }

    [Fact]
    public async Task Engine_Pull_LegacyServer_ClearsStaleRevision()
    {
        _api.V12 = false;
        await _store.ReplaceAllAsync([], string.Empty, "old", "1.8.0", revision: 7, hash: "hash-7");
        _api.Blob = _crypto.Encrypt("[{\"id\":9,\"title\":\"Remote\",\"url\":\"https://r.example\"}]", Key);

        Assert.True(await _engine.PullAsync());
        Assert.Null(await _store.GetMetaAsync(BookmarkStore.MetaRevision));
        Assert.Null(await _store.GetMetaAsync(BookmarkStore.MetaHash));
    }

    [Fact]
    public async Task Engine_Push_AmbiguousTimeout_AdoptsRemote_WhenBlobMatches()
    {
        _api.V12 = true;
        _api.ApplyThenConflict = true; // write landed, response lost
        await _store.ReplaceAllAsync(Sample(), "oldblob", "t0", "1.8.0", revision: 7, hash: "old");
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");

        await _engine.PushAsync(); // must NOT throw

        Assert.Equal(SyncStatus.Idle, _engine.Status);
        Assert.Equal("0", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
    }

    [Fact]
    public async Task Engine_Push_AmbiguousTimeout_KeepsConflict_WhenBlobDiffers()
    {
        _api.V12 = true;
        _api.ApplyThenConflict = true;
        _api.ConflictSwapBlob = "someone-elses-blob";
        await _store.ReplaceAllAsync(Sample(), "oldblob", "t0", "1.8.0", revision: 7, hash: "old");
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");

        await Assert.ThrowsAsync<DataOutOfSyncException>(() => _engine.PushAsync());
        Assert.Equal(SyncStatus.Conflict, _engine.Status);
    }

    [Fact]
    public async Task Engine_Push_AmbiguousTimeout_LegacyServer_ComparesCiphertext()
    {
        _api.V12 = false;
        _api.ApplyThenConflict = true;
        await _store.ReplaceAllAsync(Sample(), "oldblob", "t0", "1.8.0");
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");

        await _engine.PushAsync();

        Assert.Equal(SyncStatus.Idle, _engine.Status);
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
