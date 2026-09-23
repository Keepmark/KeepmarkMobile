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

    public SyncMode Mode { get; set; } = SyncMode.Auto;
    public SyncStatus Status { get; private set; } = SyncStatus.Idle;
    public DateTimeOffset? LastSyncAt { get; private set; }
    public int PendingEdits { get; private set; }

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
        try
        {
            var api = apiFor(c.ServiceUrl);
            var remote = await api.GetLastUpdatedAsync(c.Id, ct).ConfigureAwait(false);
            var local = await store.GetMetaAsync(BookmarkStore.MetaLastUpdated).ConfigureAwait(false);
            if (remote.LastUpdated == local)
            {
                log.Info("sync", "Pull: already up to date");
                LastSyncAt = DateTimeOffset.UtcNow;
                SetStatus(SyncStatus.Idle);
                return false;
            }

            var full = await api.GetBookmarksAsync(c.Id, ct).ConfigureAwait(false);
            ThrowIfVersionUnsupported(full.Version);
            var json = crypto.Decrypt(full.Bookmarks ?? string.Empty, c.DerivedKey);
            var roots = JsonSerializer.Deserialize<List<BookmarkNode>>(json, Json) ?? [];
            await store.ReplaceAllAsync(roots, full.Bookmarks ?? string.Empty, full.LastUpdated!, full.Version)
                .ConfigureAwait(false);

            if (full.Version != c.Version)
            {
                await creds.SetAsync(c with { Version = full.Version }).ConfigureAwait(false);
            }

            PendingEdits = 0;
            LastSyncAt = DateTimeOffset.UtcNow;
            SetStatus(SyncStatus.Idle);
            DataChanged?.Invoke();
            log.Info("sync", $"Pull: updated to lastUpdated={full.LastUpdated}");
            return true;
        }
        catch (XbsException ex)
        {
            SetStatus(ex is ServiceOfflineException ? SyncStatus.Offline : SyncStatus.Error);
            throw;
        }
    }

    public async Task PushAsync(bool updateVersion = false, CancellationToken ct = default)
    {
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
        try
        {
            var (roots, _, lastUpdated, _) = await store.LoadMirrorAsync().ConfigureAwait(false);
            var dirty = await store.GetMetaAsync(BookmarkStore.MetaDirty).ConfigureAwait(false);
            if (lastUpdated is null)
            {
                throw new IncompleteSyncInfoException(str.Get(Str.ErrIncompleteSyncInfo));
            }

            Normalize(roots);
            var json = JsonSerializer.Serialize(roots, Json);
            var blob = crypto.Encrypt(json, c.DerivedKey);

            var api = apiFor(c.ServiceUrl);
            var res = await api.UpdateBookmarksAsync(c.Id, blob, lastUpdated, updateVersion, ct).ConfigureAwait(false);

            await store.ReplaceAllAsync(roots, blob, res.LastUpdated!, updateVersion ? XbsConstants.SyncClientVersion : c.Version)
                .ConfigureAwait(false);
            if (updateVersion)
            {
                await creds.SetAsync(c with { Version = XbsConstants.SyncClientVersion }).ConfigureAwait(false);
            }

            PendingEdits = 0;
            LastSyncAt = DateTimeOffset.UtcNow;
            SetStatus(SyncStatus.Idle);
            DataChanged?.Invoke();
            log.Info("sync", $"Push: uploaded, new lastUpdated={res.LastUpdated} (was dirty={dirty})");
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

                await PullCoreAsync(ct).ConfigureAwait(false);
            }
            else if (dirty)
            {
                await PushCoreAsync(updateVersion: false, ct: ct).ConfigureAwait(false);
            }
            else
            {
                // Successful check, nothing to do: still stamp the check time.
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
    /// Status (Offline/Error), never as exceptions to the caller.</summary>
    public async Task NotifyLocalEditAsync(CancellationToken ct = default)
    {
        PendingEdits++;
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
        await RequireCredsAsync().ConfigureAwait(false);
        int rowId = await store.AddNodeAsync(parentRowId, node).ConfigureAwait(false);
        await NotifyLocalEditAsync(ct).ConfigureAwait(false);
        return rowId;
    }

    public async Task UpdateBookmarkAsync(
        int rowId, string? title, string? url, string? description, List<string>? tags, CancellationToken ct = default)
    {
        await RequireCredsAsync().ConfigureAwait(false);
        await store.UpdateNodeAsync(rowId, title, url, description, tags).ConfigureAwait(false);
        await NotifyLocalEditAsync(ct).ConfigureAwait(false);
    }

    public async Task<(int? ParentRowId, List<BookmarkNode> Subtree)> DeleteBookmarkAsync(int rowId, CancellationToken ct = default)
    {
        await RequireCredsAsync().ConfigureAwait(false);
        var backup = await store.DeleteNodeAsync(rowId).ConfigureAwait(false);
        await NotifyLocalEditAsync(ct).ConfigureAwait(false);
        return backup;
    }

    public async Task MoveBookmarkAsync(int rowId, int? newParentRowId, CancellationToken ct = default)
    {
        await RequireCredsAsync().ConfigureAwait(false);
        await store.MoveNodeAsync(rowId, newParentRowId).ConfigureAwait(false);
        await NotifyLocalEditAsync(ct).ConfigureAwait(false);
    }

    public async Task RestoreBookmarkAsync(int? parentRowId, List<BookmarkNode> subtree, CancellationToken ct = default)
    {
        await RequireCredsAsync().ConfigureAwait(false);
        await store.RestoreSubtreeAsync(parentRowId, subtree).ConfigureAwait(false);
        await NotifyLocalEditAsync(ct).ConfigureAwait(false);
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
