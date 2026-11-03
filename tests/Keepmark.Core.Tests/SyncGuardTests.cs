using System.Security.Cryptography;
using System.Text.Json;
using Keepmark.Core.Crypto;
using Keepmark.Core.Data;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
using Keepmark.Core.Safety;
using Keepmark.Core.Sync;

namespace Keepmark.Core.Tests;

/// <summary>A sync replaces whole trees, so the normal path takes no safety
/// snapshot. A change beyond XbsConstants.MaxSilentSyncChanges is not an
/// ordinary edit (a wipe, a bad import, another client gone wrong): the side
/// being replaced is snapshotted first.</summary>
public sealed class SyncGuardTests : IAsyncLifetime
{
    private static readonly string Key = Convert.ToBase64String(new byte[32]);

    private string _dbPath = string.Empty;
    private BookmarkStore _store = null!;
    private XCryptoService _crypto = null!;
    private FakeApi _api = null!;
    private SyncEngine _engine = null!;
    private readonly List<(string Side, string Operation, int Items)> _snapshots = [];

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"xbs-guard-{Guid.NewGuid():N}.db");
        _store = new BookmarkStore(_dbPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            new InMemoryLogger());
        await _store.InitializeAsync();
        _crypto = new XCryptoService(new XbsLocalizer(), new InMemoryLogger());
        _api = new FakeApi();
        var creds = new SyncCredentials("sync-id", "https://example.test", Key, "1.8.0");
        _engine = new SyncEngine(_ => _api, _crypto, _store, new MemCreds(creds), new XbsLocalizer(),
            new InMemoryLogger(),
            saveSafetyBackup: (side, _, operation, items, _) =>
            {
                _snapshots.Add((side, operation, items));
                return Task.CompletedTask;
            });
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private string Enc(List<BookmarkNode> tree) => _crypto.Encrypt(JsonSerializer.Serialize(tree), Key);

    private static List<BookmarkNode> Bookmarks(int count, string prefix = "n") =>
        Enumerable.Range(0, count)
            .Select(i => new BookmarkNode { Id = 100 + i, Title = $"{prefix}{i}", Url = $"https://{prefix}{i}.example" })
            .ToList();

    /// <summary>Mirror already synced at T0 with <paramref name="local"/>.</summary>
    private Task SeedAsync(List<BookmarkNode> local) =>
        _store.ReplaceAllAsync(local, Enc(local), "2024-01-01T00:00:00.000Z", "1.8.0");

    [Fact]
    public async Task MassiveDownload_SnapshotsTheLocalCopyFirst()
    {
        await SeedAsync(Bookmarks(2, "old"));
        _api.Blob = Enc(Bookmarks(9, "new"));
        _api.LastUpdated = "2024-02-02T00:00:00.000Z";

        await _engine.PullAsync();

        var snapshot = Assert.Single(_snapshots);
        Assert.Equal("local", snapshot.Side);
        Assert.Equal(SafetyOperations.SyncPull, snapshot.Operation);
        Assert.Equal(2, snapshot.Items);
        // The download still landed: the guard snapshots, it never blocks a pull.
        Assert.Equal("2024-02-02T00:00:00.000Z", await _store.GetMetaAsync(BookmarkStore.MetaLastUpdated));
    }

    [Fact]
    public async Task OrdinaryDownload_TakesNoSnapshot()
    {
        await SeedAsync(Bookmarks(3, "old"));
        _api.Blob = Enc(Bookmarks(3, "old").Append(new BookmarkNode { Id = 200, Title = "Extra", Url = "https://extra.example" }).ToList());
        _api.LastUpdated = "2024-02-02T00:00:00.000Z";

        await _engine.PullAsync();

        Assert.Empty(_snapshots);
    }

    [Fact]
    public async Task MassiveUpload_SnapshotsTheServerCopyFirst()
    {
        await SeedAsync(Bookmarks(2, "old"));
        _api.Blob = Enc(Bookmarks(2, "old"));
        await _store.ReplaceAllAsync(Bookmarks(9, "bulk"), Enc(Bookmarks(2, "old")), "2024-01-01T00:00:00.000Z", "1.8.0");

        await _engine.PushAsync();

        var snapshot = Assert.Single(_snapshots);
        Assert.Equal("remote", snapshot.Side);
        Assert.Equal(SafetyOperations.SyncPush, snapshot.Operation);
        Assert.Equal(2, snapshot.Items);
        Assert.Equal(1, _api.PushCount);
    }

    [Fact]
    public async Task OrdinaryUpload_TakesNoSnapshot()
    {
        await SeedAsync(Bookmarks(2, "old"));
        _api.Blob = Enc(Bookmarks(2, "old"));
        var edited = Bookmarks(2, "old").Append(new BookmarkNode { Id = 300, Title = "One", Url = "https://one.example" }).ToList();
        await _store.ReplaceAllAsync(edited, Enc(Bookmarks(2, "old")), "2024-01-01T00:00:00.000Z", "1.8.0");

        await _engine.PushAsync();

        Assert.Empty(_snapshots);
        Assert.Equal(1, _api.PushCount);
    }
}
