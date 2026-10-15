using System.Text.Json;
using Keepmark.Core.Api;
using Keepmark.Core.Crypto;
using Keepmark.Core.Data;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
using Keepmark.Core.Sync;

namespace Keepmark.Core.Tests;

public sealed class SyncPerformanceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sync-perf-{Guid.NewGuid():N}.db");
    private readonly BookmarkStore _store;
    private readonly BlockingApi _api = new();
    private readonly XCryptoService _crypto = new(new XbsLocalizer(), new InMemoryLogger());
    private readonly SyncEngine _engine;
    private readonly ControlledBatchClock _clock = new();
    private static readonly string Key = Convert.ToBase64String(new byte[32]);

    public SyncPerformanceTests()
    {
        _store = new BookmarkStore(_path, Convert.ToHexString(new byte[32]), new InMemoryLogger());
        _engine = new SyncEngine(_ => _api, _crypto, _store,
            new MemCreds(new SyncCredentials("id", "https://test.invalid", Key, "1.8.0")),
            new XbsLocalizer(), new InMemoryLogger(), timeProvider: _clock);
    }

    public async Task InitializeAsync()
    {
        await _store.InitializeAsync();
        await _store.ReplaceAllAsync([], "", _api.Inner.LastUpdated, "1.8.0");
    }

    public async Task DisposeAsync()
    {
        _engine.AutoSyncEnabled = false;
        _clock.Release();
        _api.Release.TrySetResult();
        await _engine.WaitForBackgroundPushAsync();
        await _store.DisposeAsync();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
            File.Delete(file);
    }

    [Fact]
    public async Task BurstEditsUploadOneFinalTree()
    {
        // Flush only after every durable edit has been queued. This verifies
        // batching independent of CPU load or SQLite timing on the test host.
        _clock.Hold = true;
        for (var i = 0; i < 20; i++)
            await _engine.AddBookmarkAsync(null, new BookmarkNode { Title = $"Item {i}", Url = $"https://example.test/{i}" });
        await _clock.WaitForTimerAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Release();
        await _engine.WaitForBackgroundPushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _api.Inner.PushCount);
        var tree = JsonSerializer.Deserialize<List<BookmarkNode>>(_crypto.Decrypt(_api.Inner.Blob, Key))!;
        Assert.Equal(20, tree.Count);
        Assert.Equal("0", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
        Assert.Equal(0, _engine.PendingEdits);
    }

    [Fact]
    public async Task EditDuringBatchDelayRestartsQuietIntervalBeforeUpload()
    {
        _clock.Hold = true;
        await _engine.AddBookmarkAsync(null, new BookmarkNode { Title = "One", Url = "https://example.test/1" });
        await _clock.WaitForTimerAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await _engine.AddBookmarkAsync(null, new BookmarkNode { Title = "Two", Url = "https://example.test/2" });
        _clock.Release(releaseFuture: false);
        await _clock.WaitForTimerAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // The worker has reached a second delay, proving it deferred the PUT
        // after seeing a new generation under the gate.
        Assert.Equal(0, _api.Attempts);
        _clock.Release();
        await _engine.WaitForBackgroundPushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _api.Inner.PushCount);
        Assert.Equal(2, JsonSerializer.Deserialize<List<BookmarkNode>>(_crypto.Decrypt(_api.Inner.Blob, Key))!.Count);
    }

    [Fact]
    public async Task ManualUploadConsumesQueuedBurstWithoutDuplicatePut()
    {
        await _engine.AddBookmarkAsync(null, new BookmarkNode { Title = "One", Url = "https://example.test" });
        await _engine.PushAsync();
        await _engine.WaitForBackgroundPushAsync();
        Assert.Equal(1, _api.Inner.PushCount);
    }

    [Fact]
    public async Task EditWaitingOnActivePutIsEventuallyUploaded()
    {
        _api.Block = true;
        await _engine.AddBookmarkAsync(null, new BookmarkNode { Title = "One", Url = "https://example.test/1" });
        await _api.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var laterEdit = _engine.AddBookmarkAsync(null, new BookmarkNode { Title = "Two", Url = "https://example.test/2" });
        Assert.False(laterEdit.IsCompleted);
        _api.Release.TrySetResult();
        await laterEdit;
        await _engine.WaitForBackgroundPushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, _api.Inner.PushCount);
        var tree = JsonSerializer.Deserialize<List<BookmarkNode>>(_crypto.Decrypt(_api.Inner.Blob, Key))!;
        Assert.Equal(2, tree.Count);
        Assert.Equal("0", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
    }

    [Fact]
    public async Task PausedQueuedPushPreservesDirtyThenResumesOnce()
    {
        await _engine.AddBookmarkAsync(null, new BookmarkNode { Title = "One", Url = "https://example.test" });
        _engine.AutoSyncEnabled = false;
        await _engine.WaitForBackgroundPushAsync();
        Assert.Equal(0, _api.Inner.PushCount);
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
        await _engine.ExecuteAutoSyncAsync();
        Assert.Equal(0, _api.Inner.PushCount);
        _engine.AutoSyncEnabled = true;
        await _engine.ExecuteAutoSyncAsync();
        Assert.Equal(1, _api.Inner.PushCount);
    }

    [Fact]
    public async Task QueuedPushDoesNotRetryConflictEstablishedByManualPush()
    {
        await _engine.AddBookmarkAsync(null, new BookmarkNode { Title = "One", Url = "https://example.test" });
        _api.Inner.FailConflict = true;
        await Assert.ThrowsAsync<DataOutOfSyncException>(() => _engine.PushAsync());
        var attempts = _api.Attempts;
        await _engine.WaitForBackgroundPushAsync();
        Assert.Equal(attempts, _api.Attempts);
        Assert.Equal(SyncStatus.Conflict, _engine.Status);
        Assert.Equal("1", await _store.GetMetaAsync(BookmarkStore.MetaDirty));
    }

    [Fact]
    public async Task OrdinaryPushPreservesRowIdsAndAvoidsDataInvalidation()
    {
        _engine.Mode = SyncMode.Manual;
        int id = await _engine.AddBookmarkAsync(null, new BookmarkNode { Title = "One", Url = "https://example.test" });
        var changes = 0;
        _engine.DataChanged += () => changes++;
        await _engine.PushAsync();
        Assert.Equal(id, (await _store.GetChildrenAsync(null)).Single().RowId);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task AutomaticPullReusesAlreadyReadRemoteTimestamp()
    {
        _api.Inner.Blob = _crypto.Encrypt("[]", Key);
        _api.Inner.LastUpdated = "2025-01-01T00:00:00.000Z";
        await _engine.ExecuteAutoSyncAsync();
        Assert.Equal(1, _api.LastUpdatedReads);
        Assert.Equal(SyncStatus.Idle, _engine.Status);
    }

    [Fact]
    public async Task CancelledAutomaticPullDoesNotLeaveSyncingStatus()
    {
        _api.Inner.LastUpdated = "2025-01-01T00:00:00.000Z";
        using var cts = new CancellationTokenSource();
        _api.CancelDownload = cts;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.ExecuteAutoSyncAsync(cts.Token));
        Assert.Equal(SyncStatus.Idle, _engine.Status);
    }

    private sealed class ControlledBatchClock : TimeProvider
    {
        private readonly object _lock = new();
        private ManualTimer? _timer;
        private bool _hold;
        private readonly SemaphoreSlim _created = new(0);
        public bool Hold { get { lock (_lock) return _hold; } set { lock (_lock) _hold = value; } }
        public Task WaitForTimerAsync() => _created.WaitAsync();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                if (!_hold) return base.CreateTimer(callback, state, dueTime, period);
                _timer = new ManualTimer(callback, state);
                _created.Release();
                return _timer;
            }
        }

        public void Release(bool releaseFuture = true)
        {
            ManualTimer? timer;
            lock (_lock) { if (releaseFuture) _hold = false; timer = _timer; _timer = null; }
            timer?.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingApi : IXSyncApiClient
    {
        public FakeApi Inner { get; } = new();
        public bool Block { get; set; }
        public int Attempts { get; private set; }
        public int LastUpdatedReads { get; private set; }
        public CancellationTokenSource? CancelDownload { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<UpdateBookmarksResponse> UpdateBookmarksAsync(string id, string blob, string lastUpdated, bool updateVersion = false, CancellationToken ct = default)
        {
            Attempts++;
            if (Block)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await Inner.UpdateBookmarksAsync(id, blob, lastUpdated, updateVersion, ct);
        }
        public Task<ServiceInfoResponse> GetInfoAsync(CancellationToken ct = default) => Inner.GetInfoAsync(ct);
        public Task<ServiceInfoResponse> GetInfoV12Async(CancellationToken ct = default) => Inner.GetInfoV12Async(ct);
        public Task<CreateSyncResponse> CreateSyncAsync(CancellationToken ct = default) => Inner.CreateSyncAsync(ct);
        public Task<GetBookmarksResponse> GetBookmarksAsync(string id, CancellationToken ct = default)
        {
            if (CancelDownload is not null)
            {
                CancelDownload.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return Inner.GetBookmarksAsync(id, ct);
        }
        public Task<GetBookmarksResponse> GetBookmarksV12Async(string id, CancellationToken ct = default) => Inner.GetBookmarksV12Async(id, ct);
        public Task<GetBookmarksResponse?> GetBookmarksIfChangedAsync(string id, string hash, CancellationToken ct = default) => Inner.GetBookmarksIfChangedAsync(id, hash, ct);
        public Task<LastUpdatedResponse> GetLastUpdatedAsync(string id, CancellationToken ct = default)
        {
            LastUpdatedReads++;
            return Inner.GetLastUpdatedAsync(id, ct);
        }
        public Task<VersionResponse> GetVersionAsync(string id, CancellationToken ct = default) => Inner.GetVersionAsync(id, ct);
        public Task<UpdateBookmarksResponse> UpdateBookmarksV12Async(string id, string blob, long revision, bool updateVersion = false, CancellationToken ct = default) => Inner.UpdateBookmarksV12Async(id, blob, revision, updateVersion, ct);
    }
}
