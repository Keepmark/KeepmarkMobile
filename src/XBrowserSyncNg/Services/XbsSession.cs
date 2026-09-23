using System.Net.Http;
using XBrowserSyncNg.Core.Api;
using XBrowserSyncNg.Core.Crypto;
using XBrowserSyncNg.Core.Data;
using XBrowserSyncNg.Core.Errors;
using XBrowserSyncNg.Core.I18n;
using XBrowserSyncNg.Core.Logging;
using XBrowserSyncNg.Core.Models;
using XBrowserSyncNg.Core.Sync;

namespace XBrowserSyncNg.Services;

// App-scoped session: owns the encrypted store + sync engine, runs the auto
// poll timer, and drives the onboarding reconciliation flow.
// Raw passwords exist only as method arguments (never stored, never logged).

public sealed class XbsSession : IAsyncDisposable
{
    private readonly SecureCredentialStore _creds;
    private readonly XbsSettings _settings;
    private readonly IXbsLocalizer _str;
    private readonly InMemoryLogger _log = new();
    private readonly XCryptoService _crypto;
    private readonly Dictionary<string, XSyncApiClient> _apiClients = new();
    private PeriodicTimer? _pollTimer;
    private CancellationTokenSource? _pollCts;

    public BookmarkStore? Store { get; private set; }
    public SyncEngine? Engine { get; private set; }
    public bool Ready { get; private set; }

    public IXbsLogger Log => _log;

    public XbsSession(SecureCredentialStore creds, XbsSettings settings, IXbsLocalizer str)
    {
        _creds = creds;
        _settings = settings;
        _str = str;
        _log.MinLevel = settings.Verbosity;
        _crypto = new XCryptoService(str, _log);
    }

    public async Task InitAsync()
    {
        var dbKey = await _creds.GetOrCreateDbKeyAsync().ConfigureAwait(false);
        var store = new BookmarkStore(
            Path.Combine(FileSystem.AppDataDirectory, "xbs.db"), dbKey, _log);
        await store.InitializeAsync().ConfigureAwait(false);
        Store = store;

        var engine = new SyncEngine(ApiFor, _crypto, store, _creds, _str, _log)
        {
            Mode = _settings.Mode
        };
        Engine = engine;
        Ready = true;
        StartPollTimer();

        // Cold-start background sync (auto mode only, credentials present).
        // The UI shows a top loader while Engine.Status == Syncing.
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _creds.GetAsync().ConfigureAwait(false) is not null)
                {
                    await engine.ExecuteAutoSyncAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("session", $"Cold-start sync failed: {ex.GetType().Name}");
            }
        });
    }

    public XSyncApiClient ApiFor(string serviceUrl)
    {
        if (!_apiClients.TryGetValue(serviceUrl, out var client))
        {
            client = XSyncApiClient.ForService(serviceUrl, _str, _log);
            _apiClients[serviceUrl] = client;
        }

        return client;
    }

    public async Task<ServiceInfoResponse> CheckServiceAsync(string serviceUrl, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Core.XbsConstants.ServiceProbeTimeout);
        var info = await ApiFor(serviceUrl).GetInfoAsync(cts.Token).ConfigureAwait(false);
        if (info.Version is not null &&
            Core.XbsConstants.CompareVersions(info.Version, Core.XbsConstants.MinApiVersion) < 0)
        {
            throw new UnsupportedApiVersionException(_str.Get(Str.ErrUnsupportedApiVersion, info.Version, Core.XbsConstants.MinApiVersion));
        }

        return info;
    }

    /// <summary>Onboarding step: validate credentials + fetch both counts for the reconcile screen.</summary>
    public async Task<ReconcileInfo> ProbeLinkAsync(string serviceUrl, string syncId, string password, CancellationToken ct = default)
    {
        var derived = _crypto.DeriveKey(password, syncId, Core.XbsConstants.SyncClientVersion);
        var candidate = new SyncCredentials(syncId, serviceUrl, derived, Core.XbsConstants.SyncClientVersion);
        var (local, remote, remoteLast, remoteVer, remoteBlob, localBlob) =
            await Engine!.CompareAsync(candidate, ct).ConfigureAwait(false);
        return new ReconcileInfo(candidate, local, remote, remoteLast, remoteVer, remoteBlob, localBlob);
    }

    public async Task ApplyRemoteAsync(ReconcileInfo info, CancellationToken ct = default)
    {
        // Safety backup of the discarded local side (plaintext — user is warned in UI).
        await SaveSafetyBackupAsync("local", info.LocalBlob).ConfigureAwait(false);
        await _creds.SetAsync(info.Candidate).ConfigureAwait(false);
        await Engine!.PullAsync(ct).ConfigureAwait(false);
    }

    public async Task ApplyLocalAsync(ReconcileInfo info, CancellationToken ct = default)
    {
        // Safety backup of the overwritten remote side.
        await SaveSafetyBackupAsync("remote", info.RemoteBlob).ConfigureAwait(false);
        await _creds.SetAsync(info.Candidate).ConfigureAwait(false);
        await UploadMirrorAsync(info.Candidate, ct).ConfigureAwait(false);
    }

    /// <summary>Fresh/empty link: upload the current local mirror to the server.</summary>
    private async Task UploadMirrorAsync(SyncCredentials candidate, CancellationToken ct)
    {
        var (roots, _, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
        var api = ApiFor(candidate.ServiceUrl);
        var json = System.Text.Json.JsonSerializer.Serialize(roots);
        var blob = _crypto.Encrypt(json, candidate.DerivedKey);
        // lastUpdated "" forces no precondition on first upload of a fresh/empty sync slot.
        var res = await api.UpdateBookmarksAsync(candidate.Id, blob, await CurrentLastUpdatedAsync().ConfigureAwait(false) ?? string.Empty, ct: ct)
            .ConfigureAwait(false);
        await Store.ReplaceAllAsync(roots, blob, res.LastUpdated!, candidate.Version).ConfigureAwait(false);
    }

    public async Task CreateNewSyncAsync(string serviceUrl, string password, CancellationToken ct = default)
    {
        // Safety backup: the local tree is about to be replaced by an empty sync.
        var (_, localBlob, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
        await SaveSafetyBackupAsync("local", localBlob ?? string.Empty).ConfigureAwait(false);
        var created = await ApiFor(serviceUrl).CreateSyncAsync(ct).ConfigureAwait(false);
        var derived = _crypto.DeriveKey(password, created.Id!, created.Version);
        await _creds.SetAsync(new SyncCredentials(created.Id!, serviceUrl, derived, created.Version)).ConfigureAwait(false);
        await Store.ReplaceAllAsync([], string.Empty, created.LastUpdated!, created.Version).ConfigureAwait(false);
        Engine!.Mode = _settings.Mode;
    }

    /// <summary>Creates the sync on the service, then uploads the current local tree into it.
    /// Returns the new sync ID (the caller must show it: it is not derivable later).</summary>
    public async Task<string> CreateNewSyncWithUploadAsync(string serviceUrl, string password, CancellationToken ct = default)
    {
        var created = await ApiFor(serviceUrl).CreateSyncAsync(ct).ConfigureAwait(false);
        var derived = _crypto.DeriveKey(password, created.Id!, created.Version);
        var candidate = new SyncCredentials(created.Id!, serviceUrl, derived, created.Version);
        await _creds.SetAsync(candidate).ConfigureAwait(false);
        await UploadMirrorAsync(candidate, ct).ConfigureAwait(false);
        Engine!.Mode = _settings.Mode;
        return created.Id!;
    }

    public void NotifySettingsChanged()
    {
        _log.MinLevel = _settings.Verbosity;
        if (Engine is not null)
        {
            Engine.Mode = _settings.Mode;
        }
    }

    private async Task<string?> CurrentLastUpdatedAsync() =>
        await Store!.GetMetaAsync(BookmarkStore.MetaLastUpdated).ConfigureAwait(false);

    /// <summary>Encrypted local backup (password chosen by the user) + import.
    /// Same verified envelope as sync crypto: PBKDF2-SHA256/250k with a random
    /// salt stored in the file, AES-GCM + lzutf8 payload.</summary>
    public async Task<string> ExportBackupAsync(string password, CancellationToken ct = default)
    {
        BackupGuard.RequirePassword(password);
        var (roots, _, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
        var json = System.Text.Json.JsonSerializer.Serialize(roots);
        var salt = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var derived = _crypto.DeriveKey(password, salt, Core.XbsConstants.SyncClientVersion);
        var envelope = new BackupEnvelope("xBrowserSyncNg", 1, salt, _crypto.Encrypt(json, derived));
        var dir = Path.Combine(FileSystem.AppDataDirectory, "backups");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"xbs-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xbs.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(envelope), ct).ConfigureAwait(false);
        _log.Info("session", $"Encrypted backup exported ({CountBackupNodes(roots)} nodes)");
        return path;
    }

    /// <summary>Reads a backup file: validates the password and returns the item count.</summary>
    public async Task<int> PreviewBackupAsync(string path, string password, CancellationToken ct = default)
    {
        BackupGuard.RequirePath(path);
        BackupGuard.RequirePassword(password);
        var roots = await OpenBackupAsync(path, password, ct).ConfigureAwait(false);
        return BackupGuard.ValidateTree(roots);
    }

    /// <summary>Replaces the local tree with a backup (current local side safety-backed-up first).</summary>
    public async Task<int> ImportBackupAsync(string path, string password, CancellationToken ct = default)
    {
        BackupGuard.RequirePath(path);
        BackupGuard.RequirePassword(password);
        var roots = await OpenBackupAsync(path, password, ct).ConfigureAwait(false);
        var count = BackupGuard.ValidateTree(roots);
        var (_, localBlob, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
        await SaveSafetyBackupAsync("local", localBlob ?? string.Empty).ConfigureAwait(false);
        await Store.RestoreLocalAsync(roots).ConfigureAwait(false);
        return count;
    }

    private async Task<List<BookmarkNode>> OpenBackupAsync(string path, string password, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Backup file not found — pick it again.");
        }

        if (new FileInfo(path).Length > BackupGuard.MaxBytes)
        {
            throw new InvalidOperationException(
                $"Backup file is too large (over {BackupGuard.MaxBytes / 1024 / 1024} MB).");
        }

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Backup file cannot be read.", ex);
        }

        BackupEnvelope? envelope;
        try
        {
            envelope = System.Text.Json.JsonSerializer.Deserialize<BackupEnvelope>(raw);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException("Not a backup file or the file is damaged.", ex);
        }

        if (envelope is null || envelope.App != "xBrowserSyncNg" || envelope.Format != 1 ||
            string.IsNullOrEmpty(envelope.Salt) || string.IsNullOrEmpty(envelope.Blob))
        {
            throw new InvalidOperationException("Not a backup file.");
        }

        // Damaged file vs wrong password: base64 must parse before GCM runs,
        // otherwise every corruption looks like bad credentials.
        try
        {
            _ = Convert.FromBase64String(envelope.Salt);
            _ = Convert.FromBase64String(envelope.Blob);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Backup file is damaged.", ex);
        }

        var derived = _crypto.DeriveKey(password, envelope.Salt, Core.XbsConstants.SyncClientVersion);
        // Wrong password: GCM authentication fails here with an invalid-credentials error.
        var json = _crypto.Decrypt(envelope.Blob, derived);
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<BookmarkNode>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException("Backup decrypts but its content is damaged.", ex);
        }
    }

    private static int CountBackupNodes(List<BookmarkNode> roots)
    {
        int n = 0;
        var queue = new Queue<BookmarkNode>(roots);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            n++;
            if (cur.Children is not null)
            {
                foreach (var c in cur.Children)
                {
                    queue.Enqueue(c);
                }
            }
        }

        return n;
    }

    /// <summary>Safety backup of a side about to be overwritten (plaintext JSON, app-private dir).</summary>
    public async Task<string> SaveSafetyBackupAsync(string side, string plaintextJson)
    {
        var dir = Path.Combine(FileSystem.AppDataDirectory, "safety-backups");
        Directory.CreateDirectory(dir);
        // Keep only the 5 most recent safety backups.
        foreach (var old in new DirectoryInfo(dir).GetFiles("*.json")
                     .OrderByDescending(f => f.CreationTimeUtc).Skip(5))
        {
            old.Delete();
        }

        var path = Path.Combine(dir, $"safety-{side}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, plaintextJson).ConfigureAwait(false);
        _log.Info("session", $"Safety backup written ({side})");
        return path;
    }

    private void StartPollTimer()
    {
        _pollCts = new CancellationTokenSource();
        _pollTimer = new PeriodicTimer(Core.XbsConstants.SyncPollInterval);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _pollTimer.WaitForNextTickAsync(_pollCts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        if (Engine is not null)
                        {
                            await Engine.ExecuteAutoSyncAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("session", $"Background poll failed: {ex.GetType().Name}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        _pollTimer?.Dispose();
        _pollCts?.Dispose();
        foreach (var c in _apiClients.Values)
        {
            c.Dispose();
        }

        if (Store is not null)
        {
            await Store.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed record ReconcileInfo(
    SyncCredentials Candidate, int LocalCount, int RemoteCount,
    string? RemoteLastUpdated, string? RemoteVersion,
    string RemoteBlob, string LocalBlob);

public sealed record BackupEnvelope(string App, int Format, string Salt, string Blob);
