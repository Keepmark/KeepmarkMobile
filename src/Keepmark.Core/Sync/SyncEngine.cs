using System.Text.Json;
using Keepmark.Core.Api;
using Keepmark.Core.Crypto;
using Keepmark.Core.Data;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
using Keepmark.Core.Safety;

namespace Keepmark.Core.Sync;

public enum SyncMode
{
    Auto,
    Manual
}

public enum SyncStatus
{
    Idle,
    Syncing,
    Offline,
    Conflict,
    Error,
    NoCredentials
}

/// <summary>Direction for resolving a sync conflict.</summary>
public enum ConflictResolutionDirection
{
    Pull,   // Take remote, overwrite local
    Push,   // Upload local, overwrite remote
    Merge   // Union both sides
}

/// <summary>Credentials + derived key, supplied by the platform layer (KeyStore/SecureStorage).</summary>
public sealed record SyncCredentials(string Id, string ServiceUrl, string DerivedKey, string? Version);

public interface ICredentialStore
{
    Task<SyncCredentials?> GetAsync();
    Task SetAsync(SyncCredentials creds);
    Task ClearAsync();
}

/// <summary>
/// Whole-blob sync engine (the API has no deltas: every push uploads the full tree).
/// Auto mode: pull-on-resume/poll + push on every edit. Manual: everything explicit.
/// UI preserves scroll/query across DataChanged; engine never touches UI state.
/// </summary>
public sealed class SyncEngine(
    Func<string, IXSyncApiClient> apiFor,
    ICryptoService crypto,
    BookmarkStore store,
    ICredentialStore creds,
    IXbsLocalizer str,
    IXbsLogger log,
    Func<string, string, string, int, string?, Task>? saveSafetyBackup = null,
    Action? suspendAutoSync = null,
    Action? resumeAutoSync = null,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _backgroundLock = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private Task _backgroundPush = Task.CompletedTask;
    private bool _backgroundRunning;
    private long _editGeneration;
    private volatile bool _autoSyncEnabled = true;

    /// <summary>Platform visibility gate. Pausing preserves durable dirty edits;
    /// an upload already sent is allowed to finish and record its outcome.</summary>
    public bool AutoSyncEnabled
    {
        get => _autoSyncEnabled;
        set => _autoSyncEnabled = value;
    }

    /// <summary>Drain before closing the session's store or API clients.</summary>
    public async Task WaitForBackgroundPushAsync()
    {
        while (true)
        {
            Task current;
            lock (_backgroundLock) current = _backgroundPush;
            await current.ConfigureAwait(false);
            lock (_backgroundLock)
            {
                if (ReferenceEquals(current, _backgroundPush)) return;
            }
        }
    }

    /// <summary>v1.2 capability per service URL (null = not probed yet).
    /// Probed via /info capabilities; legacy servers stay on the 1.1 contract.</summary>
    private readonly Dictionary<string, bool> _v12 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional callback for safety backups (provided by XbsSession).</summary>
    private readonly Func<string, string, string, int, string?, Task>? _saveSafetyBackup = saveSafetyBackup;

    /// <summary>Callback to suspend auto-sync timer during conflict resolution.</summary>
    private readonly Action? _suspendAutoSync = suspendAutoSync;

    /// <summary>Callback to resume auto-sync timer after conflict resolution.</summary>
    private readonly Action? _resumeAutoSync = resumeAutoSync;

    /// <summary>True while a conflict resolution (pull/push/merge) is in progress.
    /// Prevents ForceSyncAsync from detecting a false conflict during resolution.
    /// Volatile to ensure visibility across threads without locks.</summary>
    private volatile bool _resolvingConflict;

    public SyncMode Mode { get; set; } = SyncMode.Auto;
    public SyncStatus Status { get; private set; } = SyncStatus.Idle;
    public DateTimeOffset? LastSyncAt { get; private set; }
    private int _pendingEdits;
    public int PendingEdits => _pendingEdits;

    public event Action? DataChanged;
    public event Action<SyncStatus>? StatusChanged;

    /// <summary>Explicit push base: the remote state just read by the caller
    /// (conflict resolution, link upload, password change). Pushing on the
    /// stored mirror base there would always 409.</summary>
    private sealed record PushBase(string LastUpdated, long? Revision);

    /// <summary>Acquires the engine gate with a deadlock guard. Callers release
    /// it exactly once, in their own finally.</summary>
    private async Task AcquireGateAsync(string op, TimeSpan timeout, CancellationToken ct)
    {
        using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        gateCts.CancelAfter(timeout);
        try
        {
            await _gate.WaitAsync(gateCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.Error("sync", $"{op}: gate wait timed out after {(int)timeout.TotalSeconds}s");
            throw new ServiceOfflineException(Loc.Get(Str.ErrSyncBusy, op));
        }
    }

    /// <summary>Decrypted tree JSON → nodes. An empty blob (fresh sync created
    /// without an upload) is an empty tree, never a parse error.</summary>
    private static List<BookmarkNode> ParseTree(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<BookmarkNode>>(json, Json) ?? [];

    public async Task<bool> PullAsync(CancellationToken ct = default)
    {
        await AcquireGateAsync("Pull", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        try
        {
            return await PullCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> PullCoreAsync(CancellationToken ct, string? knownRemoteLastUpdated = null)
    {
        var c = await RequireCredsAsync().ConfigureAwait(false);
        SetStatus(SyncStatus.Syncing);
        log.Debug("sync", "Pull: start");
        try
        {
            // Never silently discard unpushed local edits: a dirty mirror means
            // the user must resolve the conflict first (push or explicit take-remote).
            var dirty = await store.GetMetaAsync(BookmarkStore.MetaDirty).ConfigureAwait(false) == "1";
            if (dirty)
            {
                SetStatus(SyncStatus.Conflict);
                log.Warn("sync", "Pull: refused, local edits pending (dirty)");
                throw new DataOutOfSyncException(str.Get(Str.ErrDataOutOfSync));
            }

            var api = apiFor(c.ServiceUrl);
            var remote = knownRemoteLastUpdated ?? (await api.GetLastUpdatedAsync(c.Id, ct).ConfigureAwait(false)).LastUpdated;
            var local = await store.GetMetaAsync(BookmarkStore.MetaLastUpdated).ConfigureAwait(false);
            log.Debug("sync", $"Pull: remote={remote} local={local}");
            if (remote == local)
            {
                log.Info("sync", "Pull: already up to date");
                LastSyncAt = DateTimeOffset.UtcNow;
                SetStatus(SyncStatus.Idle);
                return false;
            }

            // v1.2: skip the download when the content hash matches.
            if (await IsV12Async(c.ServiceUrl, ct).ConfigureAwait(false))
            {
                var knownHash = await store.GetMetaAsync(BookmarkStore.MetaHash).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(knownHash))
                {
                    var unchanged = await api.GetBookmarksIfChangedAsync(c.Id, knownHash, ct).ConfigureAwait(false);
                    if (unchanged is null)
                    {
                        log.Info("sync", "Pull: unchanged (304)");
                        LastSyncAt = DateTimeOffset.UtcNow;
                        SetStatus(SyncStatus.Idle);
                        return false;
                    }

                    return await ApplyPulledAsync(unchanged, c, ct).ConfigureAwait(false);
                }
            }

            var full = await GetFullAsync(c, ct).ConfigureAwait(false);
            return await ApplyPulledAsync(full, c, ct).ConfigureAwait(false);
        }
        catch (DataOutOfSyncException)
        {
            SetStatus(SyncStatus.Conflict);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(SyncStatus.Idle);
            throw;
        }
        catch (XbsException ex)
        {
            SetStatus(ex is ServiceOfflineException ? SyncStatus.Offline : SyncStatus.Error);
            throw;
        }
    }

    private async Task<bool> ApplyPulledAsync(GetBookmarksResponse full, SyncCredentials c, CancellationToken ct)
    {
        ThrowIfVersionUnsupported(full.Version);
        var roots = ParseTree(crypto.Decrypt(full.Bookmarks ?? string.Empty, c.DerivedKey));
        // Meta persists only when the server speaks v1.2; otherwise stale
        // revision/hash is cleared so the next push cannot reuse it.
        await store.ReplaceAllAsync(roots, full.Bookmarks ?? string.Empty, full.LastUpdated!, full.Version,
            full.Meta?.Revision, full.Meta?.Hash).ConfigureAwait(false);

        if (full.Version != c.Version)
        {
            await creds.SetAsync(c with { Version = full.Version }).ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _pendingEdits, 0);
        LastSyncAt = DateTimeOffset.UtcNow;
        SetStatus(SyncStatus.Idle);
        DataChanged?.Invoke();
        log.Info("sync", $"Pull: updated to lastUpdated={full.LastUpdated}");
        return true;
    }

    /// <summary>Full download on the best contract: v1.2 (meta flows, so the
    /// next push can use revision CAS) when the service speaks it, else legacy.</summary>
    private async Task<GetBookmarksResponse> GetFullAsync(SyncCredentials c, CancellationToken ct)
    {
        var api = apiFor(c.ServiceUrl);
        return await IsV12Async(c.ServiceUrl, ct).ConfigureAwait(false)
            ? await api.GetBookmarksV12Async(c.Id, ct).ConfigureAwait(false)
            : await api.GetBookmarksAsync(c.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads + decrypts the remote tree (version-gated).</summary>
    private async Task<(GetBookmarksResponse Res, List<BookmarkNode> Tree)> FetchRemoteTreeAsync(
        SyncCredentials c, CancellationToken ct)
    {
        var res = await GetFullAsync(c, ct).ConfigureAwait(false);
        ThrowIfVersionUnsupported(res.Version);
        return (res, ParseTree(crypto.Decrypt(res.Bookmarks ?? string.Empty, c.DerivedKey)));
    }

    /// <summary>Capability probe, cached per service URL. Probed on the 1.2.0
    /// contract (the legacy contract strips capabilities, so probing there
    /// could never succeed). 412 / missing capabilities = legacy. A service
    /// older than MinApiVersion throws instead of being silently used.</summary>
    private async Task<bool> IsV12Async(string serviceUrl, CancellationToken ct)
    {
        if (_v12.TryGetValue(serviceUrl, out var cached))
        {
            return cached;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(XbsConstants.ServiceProbeTimeout);
            var info = await apiFor(serviceUrl).GetInfoV12Async(cts.Token).ConfigureAwait(false);
            if (info.Version is not null
                && XbsConstants.CompareVersions(info.Version, XbsConstants.MinApiVersion) < 0)
            {
                throw new UnsupportedApiVersionException(
                    str.Get(Str.ErrUnsupportedApiVersion, info.Version, XbsConstants.MinApiVersion));
            }

            cached = info.Capabilities?.Revision == true;
        }
        catch (UnsupportedApiVersionException)
        {
            throw;
        }
        catch
        {
            // Legacy server (412), offline, or malformed: stay on 1.1.
            cached = false;
        }

        _v12[serviceUrl] = cached;
        log.Debug("sync", $"Capabilities for {serviceUrl}: v12={cached}");
        return cached;
    }

    /// <summary>Best-effort check: does the server already hold exactly this blob?
    /// v1.2 uses the content hash (one conditional GET); legacy downloads and
    /// compares ciphertext. Any failure answers false (caller keeps the conflict).</summary>
    private async Task<bool> RemoteHasBlobAsync(string serviceUrl, string syncId, string blob, CancellationToken ct)
    {
        try
        {
            var api = apiFor(serviceUrl);
            if (await IsV12Async(serviceUrl, ct).ConfigureAwait(false))
            {
                return await api.GetBookmarksIfChangedAsync(syncId, BlobHash.Of(blob), ct).ConfigureAwait(false) is null;
            }

            var full = await api.GetBookmarksAsync(syncId, ct).ConfigureAwait(false);
            return string.Equals(full.Bookmarks, blob, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            log.Debug("sync", $"RemoteHasBlob check failed: {ex.GetType().Name}");
            return false;
        }
    }

    public async Task PushAsync(bool updateVersion = false, CancellationToken ct = default)
    {
        log.Debug("sync", "Push: start");
        await AcquireGateAsync("Push", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        try
        {
            await PushCoreAsync(updateVersion, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PushCoreAsync(
        bool updateVersion, CancellationToken ct, PushBase? baseOverride = null, string? keyOverride = null)
    {
        var stored = await RequireCredsAsync().ConfigureAwait(false);
        // keyOverride: password change re-encrypts under the NEW key; the
        // stored credentials rotate only after the server accepted it.
        var c = keyOverride is null ? stored : stored with { DerivedKey = keyOverride };
        SetStatus(SyncStatus.Syncing);
        List<BookmarkNode> roots = [];
        string blob = string.Empty;
        try
        {
            List<BookmarkNode> loaded;
            string? lastUpdated;
            (loaded, _, lastUpdated, _) = await store.LoadMirrorAsync(includeBlob: false).ConfigureAwait(false);
            roots = loaded;
            var dirty = await store.GetMetaAsync(BookmarkStore.MetaDirty).ConfigureAwait(false);
            if (baseOverride is not null)
            {
                lastUpdated = baseOverride.LastUpdated;
            }

            if (lastUpdated is null)
            {
                throw new IncompleteSyncInfoException(str.Get(Str.ErrIncompleteSyncInfo));
            }

            Normalize(roots);
            var json = JsonSerializer.Serialize(roots, Json);
            blob = crypto.Encrypt(json, c.DerivedKey);

            var api = apiFor(c.ServiceUrl);
            UpdateBookmarksResponse res;
            var revRaw = baseOverride is not null
                ? baseOverride.Revision?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : await store.GetMetaAsync(BookmarkStore.MetaRevision).ConfigureAwait(false);
            if (await IsV12Async(c.ServiceUrl, ct).ConfigureAwait(false)
                && long.TryParse(revRaw, out var baseRevision))
            {
                // v1.2 revision CAS: races lose with 409 instead of overwriting.
                res = await api.UpdateBookmarksV12Async(c.Id, blob, baseRevision, updateVersion, ct).ConfigureAwait(false);
            }
            else
            {
                if (revRaw is not null)
                {
                    log.Info("sync", "Push: no usable revision, falling back to timestamp path");
                }

                res = await api.UpdateBookmarksAsync(c.Id, blob, lastUpdated, updateVersion, ct).ConfigureAwait(false);
            }

            var rowsChanged = await store.AcknowledgePushAsync(blob, res.LastUpdated!,
                updateVersion ? XbsConstants.SyncClientVersion : c.Version,
                res.Meta?.Revision, res.Meta?.Hash).ConfigureAwait(false);
            if (updateVersion)
            {
                await creds.SetAsync(c with { Version = XbsConstants.SyncClientVersion }).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _pendingEdits, 0);
            LastSyncAt = DateTimeOffset.UtcNow;
            SetStatus(SyncStatus.Idle);
            if (rowsChanged) DataChanged?.Invoke();
            log.Info("sync", $"Push: uploaded, new lastUpdated={res.LastUpdated} (was dirty={dirty})");
        }
        catch (DataOutOfSyncException)
        {
            // Ambiguous timeout? If the server already holds our exact blob
            // (our push landed but the response was lost), adopt it instead
            // of reporting a phantom conflict.
            if (await RemoteHasBlobAsync(c.ServiceUrl, c.Id, blob, ct).ConfigureAwait(false))
            {
                log.Info("sync", "Push: server already had our blob, adopting");
                try
                {
                    var remote = await apiFor(c.ServiceUrl).GetBookmarksAsync(c.Id, ct).ConfigureAwait(false);
                    ThrowIfVersionUnsupported(remote.Version);
                    var rowsChanged = await store.AcknowledgePushAsync(blob, remote.LastUpdated!,
                        updateVersion ? XbsConstants.SyncClientVersion : c.Version,
                        remote.Meta?.Revision, remote.Meta?.Hash).ConfigureAwait(false);
                    if (updateVersion)
                    {
                        await creds.SetAsync(c with { Version = XbsConstants.SyncClientVersion }).ConfigureAwait(false);
                    }

                    Interlocked.Exchange(ref _pendingEdits, 0);
                    LastSyncAt = DateTimeOffset.UtcNow;
                    SetStatus(SyncStatus.Idle);
                    if (rowsChanged) DataChanged?.Invoke();
                    return;
                }
                catch (XbsException ex)
                {
                    SetStatus(ex is ServiceOfflineException ? SyncStatus.Offline : SyncStatus.Error);
                    throw;
                }
                catch (Exception)
                {
                    SetStatus(SyncStatus.Error);
                    throw;
                }
            }

            SetStatus(SyncStatus.Conflict);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(SyncStatus.Idle);
            throw;
        }
        catch (XbsException ex)
        {
            SetStatus(ex is ServiceOfflineException ? SyncStatus.Offline : SyncStatus.Error);
            throw;
        }
    }

    /// <summary>Auto tick: pull if remote is newer, else push if local is dirty.</summary>
    public async Task ExecuteAutoSyncAsync(CancellationToken ct = default)
    {
        if (Mode != SyncMode.Auto || !AutoSyncEnabled)
        {
            return;
        }

        // Acquire the gate first, then check the flag while holding it.
        try
        {
            await AcquireGateAsync("Auto-sync", TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (ServiceOfflineException)
        {
            log.Warn("sync", "Auto-sync skipped: another sync operation is running");
            return;
        }

        try
        {
            if (_resolvingConflict || !AutoSyncEnabled || Mode != SyncMode.Auto)
            {
                log.Warn("sync", "Auto-sync aborted: conflict resolution in progress");
                return;
            }

            await ForceSyncInternalAsync(ct, isAutoSync: true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Manual "Sync now": same as the auto tick but runs in any mode.</summary>
    public async Task ForceSyncAsync(CancellationToken ct = default)
    {
        await AcquireGateAsync("Sync now", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        try
        {
            await ForceSyncInternalAsync(ct, isAutoSync: false).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Internal sync logic shared by auto and manual sync. The caller
    /// holds <c>_gate</c> and releases it; this method never does.</summary>
    private async Task ForceSyncInternalAsync(CancellationToken ct, bool isAutoSync)
    {
        log.Debug("sync", isAutoSync ? "Auto-sync: start" : "Manual sync: start");

        // Check flag WHILE holding the gate (race window between check and gate).
        if (_resolvingConflict)
        {
            log.Warn("sync", "ForceSync aborted: conflict resolution in progress");
            if (isAutoSync) return;
            throw new InvalidOperationException(Loc.Get(Str.ErrConflictInProgress));
        }

        var c = await creds.GetAsync().ConfigureAwait(false);
        if (c is null)
        {
            SetStatus(SyncStatus.NoCredentials);
            return;
        }

        var api = apiFor(c.ServiceUrl);
        string? remote;
        try
        {
            remote = (await api.GetLastUpdatedAsync(c.Id, ct).ConfigureAwait(false)).LastUpdated;
        }
        catch (ServiceOfflineException)
        {
            SetStatus(SyncStatus.Offline);
            return;
        }

        var local = await store.GetMetaAsync(BookmarkStore.MetaLastUpdated).ConfigureAwait(false);
        var dirty = await store.GetMetaAsync(BookmarkStore.MetaDirty).ConfigureAwait(false) == "1";

        if (remote != local)
        {
            if (dirty)
            {
                // Both sides moved: never auto-merge, surface the conflict.
                SetStatus(SyncStatus.Conflict);
                log.Warn("sync", "Auto: remote and local both changed -> conflict");
                return;
            }

            log.Debug("sync", "ForceSync: pulling remote");
            await PullCoreAsync(ct, remote).ConfigureAwait(false);
        }
        else if (dirty)
        {
            log.Debug("sync", "ForceSync: pushing local");
            await PushCoreAsync(updateVersion: false, ct: ct).ConfigureAwait(false);
        }
        else
        {
            // Successful check, nothing to do: still stamp the check time.
            log.Debug("sync", "ForceSync: already in sync");
            LastSyncAt = DateTimeOffset.UtcNow;
            SetStatus(SyncStatus.Idle);
        }
    }

    /// <summary>Marks the mirror dirty after a local edit. In Auto mode the push
    /// runs in the background so the UI never waits for the network: the edit
    /// is already durable locally when this returns. Failures surface via
    /// Status (Offline/Error), never as exceptions to the caller.
    /// The store itself writes dirty=1 inside each mutation transaction;
    /// the SetMeta here is an idempotent safety net. Callers must hold
    /// _gate (all engine mutators do) so edits never interleave a push/pull.</summary>
    public async Task NotifyLocalEditAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pendingEdits);
        await store.SetMetaAsync(BookmarkStore.MetaDirty, "1").ConfigureAwait(false);
        lock (_backgroundLock)
        {
            _editGeneration++;
            if (Mode == SyncMode.Auto && AutoSyncEnabled && !_backgroundRunning)
            {
                _backgroundRunning = true;
                _backgroundPush = Task.Run(PushInBackgroundAsync);
            }
        }
    }

    private async Task PushInBackgroundAsync()
    {
        long processed = -1;
        var failed = false;
        try
        {
            while (true)
            {
                // Wait for a quiet batch without a timer/task per edit. If an
                // edit arrived during the delay, restart it before serializing.
                long observed;
                lock (_backgroundLock) observed = _editGeneration;
                await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider).ConfigureAwait(false);
                await AcquireGateAsync("Background push", TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                try
                {
                    lock (_backgroundLock) processed = _editGeneration;
                    if (_resolvingConflict || Status == SyncStatus.Conflict ||
                        !AutoSyncEnabled || Mode != SyncMode.Auto)
                    {
                        return;
                    }

                    if (processed != observed) continue;

                    // A manual push/pull may already have consumed this burst
                    // while this worker waited for the gate.
                    if (await store.GetMetaAsync(BookmarkStore.MetaDirty).ConfigureAwait(false) == "1")
                        await PushCoreAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }

                lock (_backgroundLock)
                {
                    if (_editGeneration == processed)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failed = true;
            // Dirty remains durable; retry on the next edit or foreground poll,
            // never spin/retry on a failing network in the background.
            log.Warn("sync", $"Background push failed: {ex.Message}");
        }
        finally
        {
            lock (_backgroundLock)
            {
                _backgroundRunning = false;
                if (!failed && _editGeneration != processed && AutoSyncEnabled && Mode == SyncMode.Auto &&
                    Status != SyncStatus.Conflict && !_resolvingConflict)
                {
                    _backgroundRunning = true;
                    _backgroundPush = Task.Run(PushInBackgroundAsync);
                }
            }
        }
    }

    public async Task<int> AddBookmarkAsync(int? parentRowId, BookmarkNode node, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            int rowId = await store.AddNodeAsync(parentRowId, node).ConfigureAwait(false);
            await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            log.Info("sync", node.Children is not null ? "Added folder" : "Added bookmark");
            return rowId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Add under the folder with the given stable NodeId (null = top
    /// level). Prefer over <see cref="AddBookmarkAsync"/>.</summary>
    public async Task<int> AddBookmarkByNodeIdAsync(int? parentNodeId, BookmarkNode node, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            int rowId = await store.AddNodeByParentNodeIdAsync(parentNodeId, node).ConfigureAwait(false);
            await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            log.Info("sync", node.Children is not null ? "Added folder" : "Added bookmark");
            return rowId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateBookmarkAsync(
        int rowId, string? title, string? url, string? description, List<string>? tags, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            await store.UpdateNodeAsync(rowId, title, url, description, tags).ConfigureAwait(false);
            await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            log.Info("sync", "Updated bookmark");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Update by stable NodeId. Prefer over <see cref="UpdateBookmarkAsync"/>.</summary>
    public async Task UpdateBookmarkByNodeIdAsync(
        int nodeId, string? title, string? url, string? description, List<string>? tags, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            await store.UpdateNodeByNodeIdAsync(nodeId, title, url, description, tags).ConfigureAwait(false);
            await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            log.Info("sync", "Updated bookmark");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(int? ParentRowId, List<BookmarkNode> Subtree)> DeleteBookmarkAsync(int rowId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            var backup = await store.DeleteNodeAsync(rowId).ConfigureAwait(false);
            await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            log.Info("sync", $"Deleted item (+{backup.Subtree.Count - 1} descendants)");
            return backup;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Single delete by stable NodeId: immune to push/pull rekey
    /// between list render and the user action. Returns the parent's stable
    /// NodeId + subtree for undo. Prefer over <see cref="DeleteBookmarkAsync"/>.</summary>
    public async Task<(int? ParentNodeId, List<BookmarkNode> Subtree)> DeleteBookmarkByNodeIdAsync(int nodeId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            var backup = await store.DeleteNodeByNodeIdAsync(nodeId).ConfigureAwait(false);
            await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            log.Info("sync", $"Deleted item (+{backup.Subtree.Count - 1} descendants)");
            return backup;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Bulk delete by stable NodeId: ONE transaction + ONE push, no
    /// matter how many rows. The only sane path for multi-delete (sync storms
    /// get services to throttle you).</summary>
    public async Task<int> DeleteBookmarksByNodeIdsAsync(IEnumerable<int> nodeIds, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            int n = await store.DeleteNodesByNodeIdsAsync(nodeIds).ConfigureAwait(false);
            if (n > 0)
            {
                await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            }

            log.Info("sync", $"Batch deleted {n} item(s)");
            return n;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MoveBookmarkAsync(int rowId, int? newParentRowId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            await store.MoveNodeAsync(rowId, newParentRowId).ConfigureAwait(false);
            await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            log.Info("sync", "Moved item");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Move by stable NodeIds. Prefer over <see cref="MoveBookmarkAsync"/>.</summary>
    public async Task MoveBookmarkByNodeIdAsync(int nodeId, int? newParentNodeId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            await store.MoveNodeByNodeIdAsync(nodeId, newParentNodeId).ConfigureAwait(false);
            await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            log.Info("sync", "Moved item");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreBookmarkAsync(int? parentRowId, List<BookmarkNode> subtree, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            await store.RestoreSubtreeAsync(parentRowId, subtree).ConfigureAwait(false);
            await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            log.Info("sync", $"Restored {subtree.Count} item(s) (undo)");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Undo-restore under the folder with the given stable NodeId
    /// (null = top level). Prefer over <see cref="RestoreBookmarkAsync"/>.</summary>
    public async Task RestoreBookmarkByNodeIdAsync(int? parentNodeId, List<BookmarkNode> subtree, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            await store.RestoreSubtreeByNodeIdAsync(parentNodeId, subtree).ConfigureAwait(false);
            await NotifyLocalEditAsync(ct).ConfigureAwait(false);
            log.Info("sync", $"Restored {subtree.Count} item(s) (undo)");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Onboarding/server-switch reconciliation: how many items on each side?
    /// Downloads + decrypts the remote tree (password already in hand).
    /// Also returns the raw blobs so the overwritten side can be safety-backed-up.
    /// </summary>
    public async Task<(int Local, int Remote, string? RemoteLastUpdated, string? RemoteVersion, string RemoteBlob, string LocalBlob)> CompareAsync(
        SyncCredentials candidate, CancellationToken ct = default)
    {
        var (_, localBlob, localLast, _) = await store.LoadMirrorAsync().ConfigureAwait(false);
        var stats = await store.GetStatsAsync().ConfigureAwait(false);
        int local = stats.Folders + stats.Bookmarks + stats.Separators;

        var api = apiFor(candidate.ServiceUrl);
        var info = await api.GetInfoAsync(ct).ConfigureAwait(false);
        if (info.Version is not null
            && XbsConstants.CompareVersions(info.Version, XbsConstants.MinApiVersion) < 0)
        {
            throw new UnsupportedApiVersionException(
                str.Get(Str.ErrUnsupportedApiVersion, info.Version, XbsConstants.MinApiVersion));
        }

        var full = await api.GetBookmarksAsync(candidate.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(full.Version) && !string.IsNullOrEmpty(full.Bookmarks))
        {
            // R2.3: version-less legacy syncs use the old crypto — decrypting
            // would only surface as a misleading "wrong password".
            throw new UnsupportedSyncVersionException(
                Loc.Get(Str.ErrLegacyReconnect));
        }

        ThrowIfVersionUnsupported(full.Version);
        var roots = ParseTree(crypto.Decrypt(full.Bookmarks ?? string.Empty, candidate.DerivedKey));
        int remote = CountNodes(roots);
        _ = localLast;
        return (local, remote, full.LastUpdated, full.Version, full.Bookmarks ?? string.Empty, localBlob ?? string.Empty);
    }

    private void ThrowIfVersionUnsupported(string? remoteVersion)
    {
        if (remoteVersion is not null && CompareVersions(remoteVersion, XbsConstants.SyncClientVersion) > 0)
        {
            throw new UnsupportedSyncVersionException(str.Get(Str.ErrUnsupportedSyncVersion, remoteVersion));
        }
    }

    private static int CompareVersions(string a, string b) => XbsConstants.CompareVersions(a, b);

    /// <summary>Shape the tree exactly like the original cleanBookmark: no empty descriptions/tags.</summary>
    private static void Normalize(List<BookmarkNode> roots)
    {
        var stack = new Stack<BookmarkNode>(roots);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (string.IsNullOrWhiteSpace(n.Description))
            {
                n.Description = null;
            }
            else if (n.Description.Length > XbsConstants.DescriptionMaxLength)
            {
                n.Description = n.Description[..XbsConstants.DescriptionMaxLength];
            }

            if (n.Tags is { Count: 0 })
            {
                n.Tags = null;
            }

            if (n.Children is not null)
            {
                foreach (var c in n.Children)
                {
                    stack.Push(c);
                }
            }
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

    /// <summary>Shared envelope for the three conflict resolutions (spec §13.1):
    /// flag + timer suspension first, gate held for the whole resolution,
    /// everything undone in ONE finally — even when the gate wait times out.</summary>
    private async Task RunConflictResolutionAsync(
        string name, Func<SyncCredentials, Task> body, CancellationToken ct)
    {
        _resolvingConflict = true;
        _suspendAutoSync?.Invoke();
        var acquired = false;
        try
        {
            await AcquireGateAsync(name, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            acquired = true;
            var c = await RequireCredsAsync().ConfigureAwait(false);
            SetStatus(SyncStatus.Syncing);
            log.Info("sync", $"Conflict resolution: {name} start");
            await body(c).ConfigureAwait(false);
            log.Info("sync", $"Conflict resolved via {name}");
        }
        catch (Exception ex)
        {
            SetStatus(ex switch
            {
                DataOutOfSyncException => SyncStatus.Conflict,
                ServiceOfflineException => SyncStatus.Offline,
                _ => SyncStatus.Error,
            });
            log.Warn("sync", $"Conflict {name} failed: {ex.GetType().Name}");
            throw;
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }

            _resolvingConflict = false;
            _resumeAutoSync?.Invoke();
        }
    }

    /// <summary>Fail-closed snapshot through the session callback. Without a
    /// callback (unit tests) nothing is written; production always wires it.</summary>
    private Task SnapshotAsync(string side, List<BookmarkNode> tree, string operation, string syncId) =>
        _saveSafetyBackup is null
            ? Task.CompletedTask
            : _saveSafetyBackup(side, JsonSerializer.Serialize(tree, Json), operation, CountNodes(tree), syncId);

    /// <summary>Resolves a conflict by taking the remote tree (pull). The local
    /// side is snapshotted first, then the dirty mirror is replaced explicitly
    /// (the normal pull refuses a dirty mirror by design).</summary>
    public Task ResolveConflictPullAsync(CancellationToken ct = default) =>
        RunConflictResolutionAsync("pull", async c =>
        {
            var (localRoots, _, _, _) = await store.LoadMirrorAsync(includeBlob: false).ConfigureAwait(false);
            await SnapshotAsync("local", localRoots, SafetyOperations.ConflictPull, c.Id).ConfigureAwait(false);
            var full = await GetFullAsync(c, ct).ConfigureAwait(false);
            await ApplyPulledAsync(full, c, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Resolves a conflict by uploading the local tree (push), based
    /// on the remote state just downloaded for the snapshot — never on the
    /// stale mirror base that caused the conflict.</summary>
    public Task ResolveConflictPushAsync(CancellationToken ct = default) =>
        RunConflictResolutionAsync("push", async c =>
        {
            var (res, remoteRoots) = await FetchRemoteTreeAsync(c, ct).ConfigureAwait(false);
            await SnapshotAsync("remote", remoteRoots, SafetyOperations.ConflictPush, c.Id).ConfigureAwait(false);
            await PushCoreAsync(false, ct, new PushBase(res.LastUpdated!, res.Meta?.Revision)).ConfigureAwait(false);
        }, ct);

    /// <summary>Resolves a conflict by merging both trees (union, never
    /// deletes). Both sides are snapshotted before the first mutation; a 409
    /// (remote moved again) re-fetches and re-merges from the ORIGINAL local
    /// tree, up to 3 attempts (spec §13.2).</summary>
    public Task ResolveConflictMergeAsync(CancellationToken ct = default) =>
        RunConflictResolutionAsync("merge", async c =>
        {
            var (localRoots, _, _, _) = await store.LoadMirrorAsync(includeBlob: false).ConfigureAwait(false);
            const int maxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                var (res, remoteRoots) = await FetchRemoteTreeAsync(c, ct).ConfigureAwait(false);
                if (attempt == 1)
                {
                    await SnapshotAsync("local", localRoots, SafetyOperations.ConflictMerge, c.Id).ConfigureAwait(false);
                    await SnapshotAsync("remote", remoteRoots, SafetyOperations.ConflictMerge, c.Id).ConfigureAwait(false);
                }

                var merged = BookmarkMerge.Merge(localRoots, remoteRoots);
                await store.RestoreLocalAsync(merged.Tree).ConfigureAwait(false);
                try
                {
                    await PushCoreAsync(false, ct, new PushBase(res.LastUpdated!, res.Meta?.Revision)).ConfigureAwait(false);
                    log.Info("sync", $"Conflict merge: pushed on attempt {attempt}");
                    return;
                }
                catch (DataOutOfSyncException) when (attempt < maxAttempts)
                {
                    log.Warn("sync", $"Conflict merge: 409 on attempt {attempt}/{maxAttempts}, re-merging");
                }
            }
        }, ct);

    /// <summary>Gets a merge preview for the conflict resolution UI. Always
    /// re-fetches the remote (spec §13.3), never cached state.</summary>
    public async Task<MergeStats> GetConflictMergePreviewAsync(CancellationToken ct = default)
    {
        var c = await RequireCredsAsync().ConfigureAwait(false);
        var (res, remoteRoots) = await FetchRemoteTreeAsync(c, ct).ConfigureAwait(false);
        var (localRoots, _, _, _) = await store.LoadMirrorAsync(includeBlob: false).ConfigureAwait(false);
        var stats = BookmarkMerge.Merge(localRoots, remoteRoots).Stats;
        return stats with { RemoteLastUpdated = res.LastUpdated };
    }

    /// <summary>Link flow "take remote": downloads the candidate's tree and
    /// replaces the local mirror (dirty or not — the caller snapshotted it and
    /// the user chose this explicitly), then switches the stored credentials.
    /// One gate hold: no background push can interleave the switch.</summary>
    public async Task LinkAdoptRemoteAsync(SyncCredentials candidate, CancellationToken ct = default)
    {
        await AcquireGateAsync("Link (take remote)", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        try
        {
            SetStatus(SyncStatus.Syncing);
            var full = await GetFullAsync(candidate, ct).ConfigureAwait(false);
            // Decrypts (wrong password throws) before the mirror is replaced;
            // credentials switch last.
            await ApplyPulledAsync(full, candidate, ct).ConfigureAwait(false);
            await creds.SetAsync(candidate with { Version = full.Version ?? candidate.Version }).ConfigureAwait(false);
        }
        catch (XbsException ex)
        {
            SetStatus(ex is ServiceOfflineException ? SyncStatus.Offline : SyncStatus.Error);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Link flow "upload": PUTs <paramref name="roots"/> onto the
    /// candidate sync based on <paramref name="remoteLastUpdated"/> (the base
    /// read at probe/create time — never the previous link's mirror base).
    /// Mirror + credentials switch only after the server accepted the write,
    /// so a 409 leaves the previous link fully intact.</summary>
    public async Task LinkUploadAsync(
        SyncCredentials candidate, List<BookmarkNode> roots, string remoteLastUpdated, CancellationToken ct = default)
    {
        await AcquireGateAsync("Link (upload)", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        try
        {
            SetStatus(SyncStatus.Syncing);
            Normalize(roots);
            var blob = crypto.Encrypt(JsonSerializer.Serialize(roots, Json), candidate.DerivedKey);
            var res = await apiFor(candidate.ServiceUrl)
                .UpdateBookmarksAsync(candidate.Id, blob, remoteLastUpdated, ct: ct).ConfigureAwait(false);
            await store.ReplaceAllAsync(roots, blob, res.LastUpdated!, candidate.Version,
                res.Meta?.Revision, res.Meta?.Hash).ConfigureAwait(false);
            await creds.SetAsync(candidate).ConfigureAwait(false);
            Interlocked.Exchange(ref _pendingEdits, 0);
            LastSyncAt = DateTimeOffset.UtcNow;
            SetStatus(SyncStatus.Idle);
            DataChanged?.Invoke();
            log.Info("sync", $"Link upload: {CountNodes(roots)} nodes");
        }
        catch (XbsException ex)
        {
            SetStatus(ex switch
            {
                DataOutOfSyncException => SyncStatus.Conflict,
                ServiceOfflineException => SyncStatus.Offline,
                _ => SyncStatus.Error,
            });
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Password change: re-encrypts the mirror under
    /// <paramref name="newDerivedKey"/> and pushes it on the remote base. The
    /// remote must be exactly what this device last synced (else 409: sync
    /// first — never overwrite unseen remote changes). The server copy is
    /// snapshotted first; the stored key rotates ONLY after the PUT landed, so
    /// a failed push leaves the old password fully working.</summary>
    public async Task ChangeKeyAsync(string newDerivedKey, string safetyOperation, CancellationToken ct = default)
    {
        await AcquireGateAsync("Change password", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        try
        {
            var c = await RequireCredsAsync().ConfigureAwait(false);
            var (res, remoteRoots) = await FetchRemoteTreeAsync(c, ct).ConfigureAwait(false);
            var local = await store.GetMetaAsync(BookmarkStore.MetaLastUpdated).ConfigureAwait(false);
            if (res.LastUpdated != local)
            {
                SetStatus(SyncStatus.Conflict);
                throw new DataOutOfSyncException(
                    Loc.Get(Str.ErrPasswordChangeStale));
            }

            await SnapshotAsync("remote", remoteRoots, safetyOperation, c.Id).ConfigureAwait(false);
            await PushCoreAsync(false, ct, new PushBase(res.LastUpdated!, res.Meta?.Revision), newDerivedKey)
                .ConfigureAwait(false);
            await creds.SetAsync(c with { DerivedKey = newDerivedKey }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Current server copy as a tree (for "remote" safety snapshots).</summary>
    public async Task<List<BookmarkNode>> GetRemoteTreeAsync(CancellationToken ct = default)
    {
        var c = await RequireCredsAsync().ConfigureAwait(false);
        return (await FetchRemoteTreeAsync(c, ct).ConfigureAwait(false)).Tree;
    }

    private async Task<SyncCredentials> RequireCredsAsync()
    {
        var c = await creds.GetAsync().ConfigureAwait(false);
        if (c is null)
        {
            SetStatus(SyncStatus.NoCredentials);
            throw new IncompleteSyncInfoException(str.Get(Str.ErrIncompleteSyncInfo));
        }

        return c;
    }

    private void SetStatus(SyncStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }
}
