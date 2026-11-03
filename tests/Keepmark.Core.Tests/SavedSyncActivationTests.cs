using System.Security.Cryptography;
using Keepmark.Core.Crypto;
using Keepmark.Core.Data;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
using Keepmark.Core.Sync;

namespace Keepmark.Core.Tests;

/// <summary>Fail-closed save of the active side before a multi-sync switch:
/// the local tree is about to be replaced, so an unsaved edit must abort.</summary>
public sealed class SavedSyncActivationTests : IAsyncLifetime
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
        _dbPath = Path.Combine(Path.GetTempPath(), $"xbs-activation-{Guid.NewGuid():N}.db");
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
        new() { Id = 1, Title = "Dev", Children = [new() { Id = 2, Title = "GitHub", Url = "https://github.com" }] },
        new() { Id = 3, Title = "News", Url = "https://news.example.com" }
    ];

    /// <summary>Mirror aligned with the fake remote, so nothing looks changed.</summary>
    private async Task AlignMirrorAsync()
    {
        var blob = _crypto.Encrypt(System.Text.Json.JsonSerializer.Serialize(Sample()), Key);
        _api.Blob = blob;
        await _store.ReplaceAllAsync(Sample(), blob, _api.LastUpdated, "1.8.0");
    }

    private Task<string?> DirtyAsync() => _store.GetMetaAsync(BookmarkStore.MetaDirty);

    [Fact]
    public async Task UpToDateActiveSideNeedsNoPush()
    {
        await AlignMirrorAsync();

        await _engine.PersistActiveSideAsync();

        Assert.Equal(0, _api.PushCount);
        Assert.Equal("0", await DirtyAsync());
        Assert.Equal(SyncStatus.Idle, _engine.Status);
    }

    [Fact]
    public async Task PendingEditsArePushedBeforeTheSwitch()
    {
        await AlignMirrorAsync();
        await _store.AddNodeAsync(null, new BookmarkNode { Title = "Mine", Url = "https://mine.example" });

        await _engine.PersistActiveSideAsync();

        Assert.Equal(1, _api.PushCount);
        Assert.Equal("0", await DirtyAsync());
        Assert.Equal(_api.LastUpdated, await _store.GetMetaAsync(BookmarkStore.MetaLastUpdated));
        Assert.NotEmpty(_api.LastPushed!);
    }

    [Fact]
    public async Task PendingConflictAbortsTheSwitch()
    {
        await AlignMirrorAsync();
        await _store.AddNodeAsync(null, new BookmarkNode { Title = "Mine", Url = "https://mine.example" });
        _api.LastUpdated = "2024-09-09T00:00:00.000Z"; // another device moved

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.PersistActiveSideAsync());

        Assert.Equal(new XbsLocalizer().Get(Str.ErrSavedConflict), ex.Message);
        Assert.Equal(0, _api.PushCount);
        Assert.Equal("1", await DirtyAsync());
    }

    [Fact]
    public async Task FailedPushAbortsTheSwitchAndKeepsTheEdits()
    {
        await AlignMirrorAsync();
        await _store.AddNodeAsync(null, new BookmarkNode { Title = "Mine", Url = "https://mine.example" });
        _api.Offline = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.PersistActiveSideAsync());

        Assert.Equal(new XbsLocalizer().Get(Str.ErrSavedNotSaved), ex.Message);
        Assert.Equal("1", await DirtyAsync());
    }

    [Fact]
    public async Task UnlinkedDeviceHasNothingToSave()
    {
        await AlignMirrorAsync();
        _creds.Creds = null;

        await _engine.PersistActiveSideAsync();

        Assert.Equal(0, _api.PushCount);
        Assert.Equal("0", await DirtyAsync());
    }
}
