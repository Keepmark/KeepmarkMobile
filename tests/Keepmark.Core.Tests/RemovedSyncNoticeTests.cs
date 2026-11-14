using System.Security.Cryptography;
using Keepmark.Core.Crypto;
using Keepmark.Core.Data;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
using Keepmark.Core.Safety;
using Keepmark.Core.Sync;

namespace Keepmark.Core.Tests;

/// <summary>A 401 (sync not found) is surfaced, never acted on: the notice
/// appears, stays quiet once dismissed for the same sync, and a successful sync
/// clears it. Copies of that tree are content-addressed.</summary>
public sealed class RemovedSyncNoticeTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private BookmarkStore _store = null!;
    private XCryptoService _crypto = null!;
    private FakeApi _api = null!;
    private MemCreds _creds = null!;
    private SyncEngine _engine = null!;

    private static readonly string Key = Convert.ToBase64String(new byte[32]);

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"xbs-gone-{Guid.NewGuid():N}.db");
        var keyHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _store = new BookmarkStore(_dbPath, keyHex, new InMemoryLogger());
        await _store.InitializeAsync();
        _crypto = new XCryptoService(new XbsLocalizer(), new InMemoryLogger());
        _api = new FakeApi();
        _creds = new MemCreds(new SyncCredentials("sync-id", "https://example.test", Key, "1.8.0"));
        _engine = new SyncEngine(_ => _api, _crypto, _store, _creds, new XbsLocalizer(), new InMemoryLogger());
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static List<BookmarkNode> Sample() =>
    [
        new() { Id = 1, Title = "Dev", Children = [new() { Id = 2, Title = "GitHub", Url = "https://github.com" }] }
    ];

    private async Task AlignMirrorAsync()
    {
        var blob = _crypto.Encrypt(System.Text.Json.JsonSerializer.Serialize(Sample()), Key);
        _api.Blob = blob;
        await _store.ReplaceAllAsync(Sample(), blob, _api.LastUpdated, "1.8.0");
    }

    [Fact]
    public async Task TheStandardSyncPathSurfacesTheNotice()
    {
        _api.NotFound = true;

        await Assert.ThrowsAsync<SyncNotFoundException>(() => _engine.ForceSyncAsync());

        Assert.NotNull(_engine.GoneCredentials);
        Assert.Equal("sync-id", _engine.GoneCredentials!.Id);
        Assert.Equal(SyncStatus.Error, _engine.Status);
    }

    [Fact]
    public async Task ADismissedNoticeStaysQuietForTheSameSync()
    {
        _api.NotFound = true;
        await Assert.ThrowsAsync<SyncNotFoundException>(() => _engine.ForceSyncAsync());

        _engine.DismissGoneSync();
        Assert.Null(_engine.GoneCredentials);

        // The same 401 again does not resurrect the cancelled notice.
        await Assert.ThrowsAsync<SyncNotFoundException>(() => _engine.ForceSyncAsync());
        Assert.Null(_engine.GoneCredentials);
    }

    [Fact]
    public async Task ASuccessfulSyncClearsTheNotice()
    {
        _api.NotFound = true;
        await Assert.ThrowsAsync<SyncNotFoundException>(() => _engine.ForceSyncAsync());
        Assert.NotNull(_engine.GoneCredentials);

        _api.NotFound = false;
        await AlignMirrorAsync();
        await _engine.ForceSyncAsync();

        Assert.Null(_engine.GoneCredentials);
        Assert.Equal(SyncStatus.Idle, _engine.Status);
    }

    [Fact]
    public async Task AManualSyncReportsWhatItMoved()
    {
        await AlignMirrorAsync();
        Assert.Equal(SyncTransfer.None, await _engine.ForceSyncAsync());

        _api.LastUpdated = "2024-02-02T00:00:00.000Z"; // another device pushed
        Assert.Equal(SyncTransfer.Received, await _engine.ForceSyncAsync());
        Assert.Equal(SyncTransfer.None, await _engine.ForceSyncAsync());

        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1"); // unsent local edit
        Assert.Equal(SyncTransfer.Sent, await _engine.ForceSyncAsync());
        Assert.Equal(1, _api.PushCount);
        Assert.Equal(SyncStatus.Idle, _engine.Status);

        _api.OfflineOnRead = true;
        Assert.Equal(SyncTransfer.None, await _engine.ForceSyncAsync());
    }

    [Fact]
    public async Task ATimestampThatMovedOverTheSameContentReceivesNothing()
    {
        // v1.2: another client pushed an identical tree. The pull answers
        // "unchanged" by hash, so the UI must not announce received bookmarks.
        _api.V12 = true;
        var blob = _crypto.Encrypt(System.Text.Json.JsonSerializer.Serialize(Sample()), Key);
        _api.Blob = blob;
        await _store.ReplaceAllAsync(Sample(), blob, _api.LastUpdated, "1.8.0", revision: 7, hash: _api.Hash);

        _api.LastUpdated = "2024-03-03T00:00:00.000Z";
        Assert.Equal(SyncTransfer.None, await _engine.ForceSyncAsync());
        Assert.Equal(SyncStatus.Idle, _engine.Status);
        Assert.Equal(0, _api.GetCount);
    }

    [Fact]
    public void CopiesAreContentAddressedByTreeHash()
    {
        var a = RemovedSyncCopies.Hash("[{\"id\":1}]");
        var b = RemovedSyncCopies.Hash("[{\"id\":1}]");
        var c = RemovedSyncCopies.Hash("[{\"id\":2}]");

        Assert.Equal(64, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal($"removed-{a}.json", RemovedSyncCopies.FileName(a));
    }
}
