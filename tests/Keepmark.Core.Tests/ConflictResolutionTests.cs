using System.Security.Cryptography;
using System.Text.Json;
using Keepmark.Core.Crypto;
using Keepmark.Core.Data;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
using Keepmark.Core.Sync;

namespace Keepmark.Core.Tests;

/// <summary>Conflict resolution, link uploads and password change against a
/// fake server with real base semantics (stale timestamp/revision → 409).
/// Regression suite for review findings K-1..K-5, K-8 (2026-10-08).</summary>
public sealed class ConflictResolutionTests : IAsyncLifetime
{
    private static readonly string Key = Convert.ToBase64String(new byte[32]);
    private static readonly string OtherKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());

    private string _dbPath = string.Empty;
    private BookmarkStore _store = null!;
    private XCryptoService _crypto = null!;
    private FakeApi _api = null!;
    private MemCreds _creds = null!;
    private SyncEngine _engine = null!;
    private readonly List<(string Side, string Operation, int Items, string SyncId)> _snapshots = [];
    private int _suspends;
    private int _resumes;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"xbs-conflict-{Guid.NewGuid():N}.db");
        _store = new BookmarkStore(_dbPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), new InMemoryLogger());
        await _store.InitializeAsync();
        _crypto = new XCryptoService(new EnglishLocalizer(), new InMemoryLogger());
        _api = new FakeApi { EnforceTimestampBase = true };
        _creds = new MemCreds(new SyncCredentials("sync-id", "https://example.test", Key, "1.8.0"));
        _engine = new SyncEngine(_ => _api, _crypto, _store, _creds, new EnglishLocalizer(), new InMemoryLogger(),
            saveSafetyBackup: (side, _, op, items, id) =>
            {
                _snapshots.Add((side, op, items, id ?? string.Empty));
                return Task.CompletedTask;
            },
            suspendAutoSync: () => _suspends++,
            resumeAutoSync: () => _resumes++);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        File.Delete(_dbPath);
    }

    private string Enc(List<BookmarkNode> tree, string? key = null) =>
        _crypto.Encrypt(JsonSerializer.Serialize(tree), key ?? Key);

    private static List<BookmarkNode> Tree(params (int Id, string Title, string Url)[] items) =>
        items.Select(i => new BookmarkNode { Id = i.Id, Title = i.Title, Url = i.Url }).ToList();

    /// <summary>Mirror synced at base T0 (revision 7), then a local edit
    /// (dirty); meanwhile another device pushed (remote at T1 / revision 8).</summary>
    private async Task SetupConflictAsync(bool v12)
    {
        _api.V12 = v12;
        var local = Tree((1, "Shared", "https://shared.example"));
        var blob = Enc(local);
        await _store.ReplaceAllAsync(local, blob, "2024-01-01T00:00:00.000Z", "1.8.0",
            v12 ? 7 : null, v12 ? BlobHash.Of(blob) : null);
        await _store.AddNodeAsync(null, new BookmarkNode { Title = "Mine", Url = "https://mine.example" });
        _api.Blob = Enc(Tree((1, "Shared", "https://shared.example"), (2, "Theirs", "https://theirs.example")));
        _api.LastUpdated = "2024-03-03T00:00:00.000Z";
        _api.Revision = 8;
    }

    private async Task<List<string?>> RemoteUrlsAsync() =>
        Flatten(JsonSerializer.Deserialize<List<BookmarkNode>>(_crypto.Decrypt(_api.Blob, Key))!).Select(n => n.Url).ToList();

    private async Task<List<string?>> LocalUrlsAsync() =>
        Flatten((await _store.LoadMirrorAsync()).Roots).Select(n => n.Url).ToList();

    private static IEnumerable<BookmarkNode> Flatten(IEnumerable<BookmarkNode> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children ?? [])));

    [Fact]
    public async Task ForceSync_Clean_DoesNotThrow_AndReleasesGateOnce()
    {
        await _store.ReplaceAllAsync([], string.Empty, _api.LastUpdated, "1.8.0");

        await _engine.ForceSyncAsync();
        await _engine.ForceSyncAsync(); // a double release would have thrown / left the gate at 2
        await _engine.ExecuteAutoSyncAsync();

        Assert.Equal(SyncStatus.Idle, _engine.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvePull_ReplacesDirtyMirror_WithRemote(bool v12)
    {
        await SetupConflictAsync(v12);

        await _engine.ResolveConflictPullAsync();

        Assert.Equal(SyncStatus.Idle, _engine.Status);
        Assert.Contains("https://theirs.example", await LocalUrlsAsync());
        Assert.DoesNotContain("https://mine.example", await LocalUrlsAsync());
        Assert.Equal("0", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
        Assert.Equal(_api.LastUpdated, (await _store.LoadMirrorAsync()).LastUpdated);
        Assert.Equal(("local", "conflict-pull"), (_snapshots.Single().Side, _snapshots.Single().Operation));
        Assert.Equal(0, _api.PushCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvePush_UploadsLocal_OnFreshRemoteBase(bool v12)
    {
        await SetupConflictAsync(v12);

        await _engine.ResolveConflictPushAsync();

        Assert.Equal(SyncStatus.Idle, _engine.Status);
        Assert.Equal(1, _api.PushCount);
        Assert.Contains("https://mine.example", await RemoteUrlsAsync());
        Assert.DoesNotContain("https://theirs.example", await RemoteUrlsAsync());
        if (v12)
        {
            Assert.Equal(8, _api.LastBaseRevision);
        }

        var snap = _snapshots.Single();
        Assert.Equal(("remote", "conflict-push", 2), (snap.Side, snap.Operation, snap.Items));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveMerge_UnionsBothSides_AndPushes(bool v12)
    {
        await SetupConflictAsync(v12);

        await _engine.ResolveConflictMergeAsync();

        Assert.Equal(SyncStatus.Idle, _engine.Status);
        var remote = await RemoteUrlsAsync();
        Assert.Contains("https://mine.example", remote);
        Assert.Contains("https://theirs.example", remote);
        Assert.Equal(remote.OrderBy(u => u), (await LocalUrlsAsync()).OrderBy(u => u));
        Assert.Equal(["local", "remote"], _snapshots.Select(x => x.Side).ToArray());
        Assert.All(_snapshots, x => Assert.Equal("conflict-merge", x.Operation));
    }

    [Fact]
    public async Task ResolveMerge_RetriesOn409_ThenSucceeds()
    {
        await SetupConflictAsync(v12: true);
        _api.ConflictsBeforeSuccess = 2; // remote moves twice during the merge

        await _engine.ResolveConflictMergeAsync();

        Assert.Equal(SyncStatus.Idle, _engine.Status);
        Assert.Equal(1, _api.PushCount);
        Assert.Equal(2, _snapshots.Count); // snapshots taken once, not per attempt
    }

    [Fact]
    public async Task ResolveMerge_GivesUpAfterThreeAttempts_WithHonestConflict()
    {
        await SetupConflictAsync(v12: true);
        _api.ConflictsBeforeSuccess = 3;

        await Assert.ThrowsAsync<DataOutOfSyncException>(() => _engine.ResolveConflictMergeAsync());

        Assert.Equal(SyncStatus.Conflict, _engine.Status);
        Assert.Equal(0, _api.PushCount);
    }

    [Fact]
    public async Task FailedResolution_NeverLeavesTheFlagStuck()
    {
        await SetupConflictAsync(v12: false);
        _api.Offline = true;

        await Assert.ThrowsAsync<ServiceOfflineException>(() => _engine.ResolveConflictPushAsync());
        Assert.Equal(1, _suspends);
        Assert.Equal(1, _resumes);

        // Flag cleared + gate released: a manual sync runs (and reports the conflict honestly).
        _api.Offline = false;
        await _engine.ForceSyncAsync();
        Assert.Equal(SyncStatus.Conflict, _engine.Status);
    }

    [Fact]
    public async Task Pull_OfEmptyFreshSync_IsAnEmptyTree_NotAParseError()
    {
        await _store.ReplaceAllAsync(Tree((1, "x", "https://x.example")), "b", "old", "1.8.0");
        _api.Blob = string.Empty; // created without upload

        Assert.True(await _engine.PullAsync());
        Assert.Empty((await _store.LoadMirrorAsync()).Roots);
    }

    [Fact]
    public async Task Compare_LegacyVersionlessSync_IsRefusedAsUnsupported()
    {
        _api.Version = null!;
        _api.Blob = "legacy-ciphertext";

        await Assert.ThrowsAsync<UnsupportedSyncVersionException>(() => _engine.CompareAsync(
            new SyncCredentials("other", "https://example.test", Key, "1.8.0")));
    }

    [Fact]
    public async Task LinkUpload_UsesTheGivenRemoteBase_NotThePreviousLinksMirrorBase()
    {
        // Mirror still carries the PREVIOUS link's base.
        await _store.ReplaceAllAsync(Tree((1, "Old", "https://old.example")), "b", "previous-link-base", "1.8.0", 3, "h");
        _api.LastUpdated = "2024-06-06T00:00:00.000Z";
        var candidate = new SyncCredentials("new-sync", "https://example.test", OtherKey, "1.8.0");

        await _engine.LinkUploadAsync(candidate, Tree((5, "Mine", "https://mine.example")), _api.LastUpdated);

        Assert.Equal(1, _api.PushCount);
        Assert.Equal("new-sync", (await _creds.GetAsync())!.Id);
        Assert.Equal(_api.LastUpdated, (await _store.LoadMirrorAsync()).LastUpdated);
    }

    [Fact]
    public async Task LinkUpload_On409_LeavesPreviousLinkAndMirrorIntact()
    {
        await _store.ReplaceAllAsync(Tree((1, "Old", "https://old.example")), "b", "previous-link-base", "1.8.0");
        var candidate = new SyncCredentials("new-sync", "https://example.test", OtherKey, "1.8.0");

        await Assert.ThrowsAsync<DataOutOfSyncException>(() =>
            _engine.LinkUploadAsync(candidate, Tree((5, "Mine", "https://mine.example")), "stale-probe-base"));

        Assert.Equal("sync-id", (await _creds.GetAsync())!.Id);
        Assert.Equal("previous-link-base", (await _store.LoadMirrorAsync()).LastUpdated);
        Assert.Contains("https://old.example", await LocalUrlsAsync());
    }

    [Fact]
    public async Task LinkAdoptRemote_ReplacesEvenADirtyMirror_ThenSwitchesCreds()
    {
        await _store.ReplaceAllAsync(Tree((1, "Old", "https://old.example")), "b", "previous-link-base", "1.8.0");
        await _store.SetMetaAsync(BookmarkStore.MetaDirty, "1");
        _api.Blob = Enc(Tree((9, "Remote", "https://remote.example")), OtherKey);
        var candidate = new SyncCredentials("new-sync", "https://example.test", OtherKey, "1.8.0");

        await _engine.LinkAdoptRemoteAsync(candidate);

        Assert.Equal(["https://remote.example"], await LocalUrlsAsync());
        Assert.Equal("new-sync", (await _creds.GetAsync())!.Id);
    }

    [Fact]
    public async Task LinkAdoptRemote_WrongPassword_ChangesNothing()
    {
        await _store.ReplaceAllAsync(Tree((1, "Old", "https://old.example")), "b", "base", "1.8.0");
        _api.Blob = Enc(Tree((9, "Remote", "https://remote.example")), OtherKey);
        var wrong = new SyncCredentials("new-sync", "https://example.test", Key, "1.8.0");

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => _engine.LinkAdoptRemoteAsync(wrong));

        Assert.Equal("sync-id", (await _creds.GetAsync())!.Id);
        Assert.Contains("https://old.example", await LocalUrlsAsync());
    }

    [Fact]
    public async Task ChangeKey_RotatesStoredKeyOnlyAfterThePushLanded()
    {
        var tree = Tree((1, "A", "https://a.example"));
        _api.Blob = Enc(tree);
        await _store.ReplaceAllAsync(tree, _api.Blob, _api.LastUpdated, "1.8.0");

        await _engine.ChangeKeyAsync(OtherKey, "change-password");

        Assert.Equal(OtherKey, (await _creds.GetAsync())!.DerivedKey);
        Assert.Equal("https://a.example", JsonSerializer.Deserialize<List<BookmarkNode>>(_crypto.Decrypt(_api.Blob, OtherKey))![0].Url);
        Assert.Equal(("remote", "change-password"), (_snapshots.Single().Side, _snapshots.Single().Operation));
    }

    [Fact]
    public async Task ChangeKey_RemoteMoved_RefusesWithoutTouchingAnything()
    {
        var tree = Tree((1, "A", "https://a.example"));
        _api.Blob = Enc(tree);
        await _store.ReplaceAllAsync(tree, _api.Blob, "older-local-base", "1.8.0");

        await Assert.ThrowsAsync<DataOutOfSyncException>(() => _engine.ChangeKeyAsync(OtherKey, "change-password"));

        Assert.Equal(Key, (await _creds.GetAsync())!.DerivedKey);
        Assert.Equal(0, _api.PushCount);
        Assert.Empty(_snapshots);
    }

    [Fact]
    public async Task ChangeKey_PushFails_KeepsOldKey()
    {
        var tree = Tree((1, "A", "https://a.example"));
        _api.Blob = Enc(tree);
        await _store.ReplaceAllAsync(tree, _api.Blob, _api.LastUpdated, "1.8.0");
        _api.Offline = true;

        await Assert.ThrowsAsync<ServiceOfflineException>(() => _engine.ChangeKeyAsync(OtherKey, "change-password"));

        Assert.Equal(Key, (await _creds.GetAsync())!.DerivedKey);
    }
}
