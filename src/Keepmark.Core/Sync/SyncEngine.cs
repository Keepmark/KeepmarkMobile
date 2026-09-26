using System.Text.Json;
using Keepmark.Core.Api;
using Keepmark.Core.Crypto;
using Keepmark.Core.Data;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;

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
    IXbsLogger log)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>v1.2 capability per service URL (null = not probed yet).
    /// Probed via /info capabilities; legacy servers stay on the 1.1 contract.</summary>
    private readonly Dictionary<string, bool> _v12 = new(StringComparer.OrdinalIgnoreCase);

    public SyncMode Mode { get; set; } = SyncMode.Auto;
    public SyncStatus Status { get; private set; } = SyncStatus.Idle;
    public DateTimeOffset? LastSyncAt { get; private set; }
    private int _pendingEdits;
    public int PendingEdits => _pendingEdits;

    public event Action? DataChanged;
    public event Action<SyncStatus>? StatusChanged;

    public async Task<bool> PullAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await PullCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> PullCoreAsync(CancellationToken ct)
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
            var remote = await api.GetLastUpdatedAsync(c.Id, ct).ConfigureAwait(false);
            var local = await store.GetMetaAsync(BookmarkStore.MetaLastUpdated).ConfigureAwait(false);
            log.Debug("sync", $"Pull: remote={remote.LastUpdated} local={local}");
            if (remote.LastUpdated == local)
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

            var full = await api.GetBookmarksAsync(c.Id, ct).ConfigureAwait(false);
            return await ApplyPulledAsync(full, c, ct).ConfigureAwait(false);
        }
        catch (DataOutOfSyncException)
        {
            SetStatus(SyncStatus.Conflict);
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
        var json = crypto.Decrypt(full.Bookmarks ?? string.Empty, c.DerivedKey);
        var roots = JsonSerializer.Deserialize<List<BookmarkNode>>(json, Json) ?? [];
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
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PushCoreAsync(updateVersion, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PushCoreAsync(bool updateVersion, CancellationToken ct)
    {
        var c = await RequireCredsAsync().ConfigureAwait(false);
        SetStatus(SyncStatus.Syncing);
        List<BookmarkNode> roots = [];
        string blob = string.Empty;
        try
        {
            List<BookmarkNode> loaded;
            string? lastUpdated;
            (loaded, _, lastUpdated, _) = await store.LoadMirrorAsync().ConfigureAwait(false);
            roots = loaded;
            var dirty = await store.GetMetaAsync(BookmarkStore.MetaDirty).ConfigureAwait(false);
            if (lastUpdated is null)
            {
                throw new IncompleteSyncInfoException(str.Get(Str.ErrIncompleteSyncInfo));
            }

            Normalize(roots);
            var json = JsonSerializer.Serialize(roots, Json);
            blob = crypto.Encrypt(json, c.DerivedKey);

            var api = apiFor(c.ServiceUrl);
            UpdateBookmarksResponse res;
            var revRaw = await store.GetMetaAsync(BookmarkStore.MetaRevision).ConfigureAwait(false);
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

            await store.ReplaceAllAsync(roots, blob, res.LastUpdated!,
                updateVersion ? XbsConstants.SyncClientVersion : c.Version,
                res.Meta?.Revision, res.Meta?.Hash).ConfigureAwait(false);
            if (updateVersion)
            {
                await creds.SetAsync(c with { Version = XbsConstants.SyncClientVersion }).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _pendingEdits, 0);
            LastSyncAt = DateTimeOffset.UtcNow;
            SetStatus(SyncStatus.Idle);
            DataChanged?.Invoke();
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
                    await store.ReplaceAllAsync(roots, blob, remote.LastUpdated!,
                        updateVersion ? XbsConstants.SyncClientVersion : c.Version,
                        remote.Meta?.Revision, remote.Meta?.Hash).ConfigureAwait(false);
                    if (updateVersion)
                    {
                        await creds.SetAsync(c with { Version = XbsConstants.SyncClientVersion }).ConfigureAwait(false);
                    }

                    Interlocked.Exchange(ref _pendingEdits, 0);
                    LastSyncAt = DateTimeOffset.UtcNow;
                    SetStatus(SyncStatus.Idle);
                    DataChanged?.Invoke();
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
        catch (XbsException ex)
        {
            SetStatus(ex is ServiceOfflineException ? SyncStatus.Offline : SyncStatus.Error);
            throw;
        }
    }

    /// <summary>Auto tick: pull if remote is newer, else push if local is dirty.</summary>
    public Task ExecuteAutoSyncAsync(CancellationToken ct = default)
    {
        if (Mode != SyncMode.Auto)
        {
            return Task.CompletedTask;
        }

        return ForceSyncAsync(ct);
    }

    /// <summary>Manual "Sync now": same as the auto tick but runs in any mode.</summary>
    public async Task ForceSyncAsync(CancellationToken ct = default)
    {
        log.Debug("sync", "ForceSync: start");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
                await PullCoreAsync(ct).ConfigureAwait(false);
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
        finally
        {
            _gate.Release();
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
        if (Mode == SyncMode.Auto)
        {
            // Fire-and-forget: overlapping pushes serialize on _gate.
            _ = PushInBackgroundAsync();
        }
    }

    private async Task PushInBackgroundAsync()
    {
        try
        {
            await PushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn("sync", $"Background push failed: {ex.Message}");
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
        ThrowIfVersionUnsupported(full.Version);
        var json = crypto.Decrypt(full.Bookmarks ?? string.Empty, candidate.DerivedKey);
        var roots = JsonSerializer.Deserialize<List<BookmarkNode>>(json, Json) ?? [];
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
