using System.Net.Http;
using Keepmark.Core.Api;
using Keepmark.Core.Crypto;
using Keepmark.Core.Data;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
using Keepmark.Core.Safety;
using Keepmark.Core.Sync;

namespace Keepmark.Services;

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
    /// <summary>Serializes the whole safety subsystem (master rotation, backup
    /// writes, index rewrites) so a rotation can never interleave a write.</summary>
    private readonly SemaphoreSlim _safetyGate = new(1, 1);
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
        _log.Info("session", $"Log started (level {_log.MinLevel}, mode {_settings.Mode})");
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
        _log.Debug("session", $"Service check: {info.Status}, API v{info.Version}");
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
        _log.Info("session", $"Probe: local={local} remote={remote}");
        return new ReconcileInfo(candidate, local, remote, remoteLast, remoteVer, remoteBlob, localBlob);
    }

    public async Task ApplyRemoteAsync(ReconcileInfo info, string operation = SafetyOperations.TakeRemote, CancellationToken ct = default)
    {
        // Safety backup of the discarded local side (live tree as plaintext JSON).
        var (localRoots, _, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
        await SaveSafetyBackupAsync("local",
            System.Text.Json.JsonSerializer.Serialize(localRoots), operation, info.LocalCount).ConfigureAwait(false);
        var oldKek = (await _creds.GetAsync().ConfigureAwait(false))?.DerivedKey;
        await _creds.SetAsync(info.Candidate).ConfigureAwait(false);
        _log.Info("session", "Link: taking remote tree");
        await Engine!.PullAsync(ct).ConfigureAwait(false);
        await RotateSafetyMasterBestEffortAsync(oldKek, info.Candidate.DerivedKey).ConfigureAwait(false);
    }

    public async Task ApplyLocalAsync(ReconcileInfo info, string operation = SafetyOperations.UploadLocal, CancellationToken ct = default)
    {
        // Safety backup of the overwritten remote side: the server blob
        // decrypted with the verified candidate key (plaintext tree JSON).
        // Empty remote (fresh slot) backs up as an empty tree.
        string remoteJson = "[]";
        if (!string.IsNullOrEmpty(info.RemoteBlob))
        {
            try
            {
                remoteJson = _crypto.Decrypt(info.RemoteBlob, info.Candidate.DerivedKey);
            }
            catch (Exception ex) when (ex is XbsException or FormatException)
            {
                throw new InvalidOperationException("The server copy cannot be decrypted — safety backup refused.", ex);
            }
        }

        await SaveSafetyBackupAsync("remote", remoteJson, operation, info.RemoteCount).ConfigureAwait(false);
        var oldKek = (await _creds.GetAsync().ConfigureAwait(false))?.DerivedKey;
        await _creds.SetAsync(info.Candidate).ConfigureAwait(false);
        _log.Info("session", "Link: uploading local tree");
        await UploadMirrorAsync(info.Candidate, ct).ConfigureAwait(false);
        await RotateSafetyMasterBestEffortAsync(oldKek, info.Candidate.DerivedKey).ConfigureAwait(false);
    }

    /// <summary>Fresh/empty link: upload the current local mirror to the server.</summary>
    private async Task UploadMirrorAsync(SyncCredentials candidate, CancellationToken ct)
    {
        var (roots, _, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
        var api = ApiFor(candidate.ServiceUrl);
        NormalizeForUpload(roots);
        var json = System.Text.Json.JsonSerializer.Serialize(roots);
        var blob = _crypto.Encrypt(json, candidate.DerivedKey);
        // lastUpdated "" forces no precondition on first upload of a fresh/empty sync slot.
        var res = await api.UpdateBookmarksAsync(candidate.Id, blob, await CurrentLastUpdatedAsync().ConfigureAwait(false) ?? string.Empty, ct: ct)
            .ConfigureAwait(false);
        await Store.ReplaceAllAsync(roots, blob, res.LastUpdated!, candidate.Version,
            res.Meta?.Revision, res.Meta?.Hash).ConfigureAwait(false);
    }

    /// <summary>Same shape as the engine push: no empty descriptions/tags,
    /// descriptions truncated like the original client.</summary>
    private static void NormalizeForUpload(List<BookmarkNode> roots)
    {
        var stack = new Stack<BookmarkNode>(roots);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (string.IsNullOrWhiteSpace(n.Description))
            {
                n.Description = null;
            }
            else if (n.Description.Length > Core.XbsConstants.DescriptionMaxLength)
            {
                n.Description = n.Description[..Core.XbsConstants.DescriptionMaxLength];
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

    public async Task CreateNewSyncAsync(string serviceUrl, string password, CancellationToken ct = default)
    {
        // Safety backup: the local tree is about to be replaced by an empty sync.
        var (roots, _, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
        await SaveSafetyBackupAsync("local", System.Text.Json.JsonSerializer.Serialize(roots),
            SafetyOperations.CreateEmpty, CountBackupNodes(roots)).ConfigureAwait(false);
        var created = await ApiFor(serviceUrl).CreateSyncAsync(ct).ConfigureAwait(false);
        _log.Info("session", "Created new empty sync");
        var derived = _crypto.DeriveKey(password, created.Id!, created.Version);
        var oldKek = (await _creds.GetAsync().ConfigureAwait(false))?.DerivedKey;
        await _creds.SetAsync(new SyncCredentials(created.Id!, serviceUrl, derived, created.Version)).ConfigureAwait(false);
        await Store.ReplaceAllAsync([], string.Empty, created.LastUpdated!, created.Version).ConfigureAwait(false);
        await RotateSafetyMasterBestEffortAsync(oldKek, derived).ConfigureAwait(false);
        Engine!.Mode = _settings.Mode;
    }

    /// <summary>Creates the sync on the service, then uploads the current local tree into it.
    /// Returns the new sync ID (the caller must show it: it is not derivable later).</summary>
    public async Task<string> CreateNewSyncWithUploadAsync(string serviceUrl, string password, CancellationToken ct = default)
    {
        var created = await ApiFor(serviceUrl).CreateSyncAsync(ct).ConfigureAwait(false);
        var derived = _crypto.DeriveKey(password, created.Id!, created.Version);
        var candidate = new SyncCredentials(created.Id!, serviceUrl, derived, created.Version);
        var oldKek = (await _creds.GetAsync().ConfigureAwait(false))?.DerivedKey;
        await _creds.SetAsync(candidate).ConfigureAwait(false);
        await UploadMirrorAsync(candidate, ct).ConfigureAwait(false);
        await RotateSafetyMasterBestEffortAsync(oldKek, derived).ConfigureAwait(false);
        Engine!.Mode = _settings.Mode;
        _log.Info("session", "Created new sync and uploaded local tree");
        return created.Id!;
    }

    /// <summary>Rotates the sync password. The current password is verified
    /// first (derived key must match the stored one) — on mismatch nothing
    /// is downloaded, backed up, or overwritten. The re-encrypted tree is
    /// pushed through the engine, so revision CAS / 409 semantics apply as
    /// usual. The old password stops working everywhere immediately.</summary>
    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(currentPassword))
        {
            throw new IncompleteSyncInfoException("Current password is required.");
        }

        if (string.IsNullOrEmpty(newPassword))
        {
            throw new IncompleteSyncInfoException("A new password is required.");
        }

        var creds = await _creds.GetAsync().ConfigureAwait(false)
            ?? throw new IncompleteSyncInfoException(_str.Get(Str.ErrIncompleteSyncInfo));
        var version = creds.Version ?? Core.XbsConstants.SyncClientVersion;
        var currentDerived = _crypto.DeriveKey(currentPassword, creds.Id, version);
        if (currentDerived != creds.DerivedKey)
        {
            throw new InvalidCredentialsException("Current password is incorrect.");
        }

        // Safety backup of the server copy we are about to overwrite (live tree as plaintext JSON).
        var (treeRoots, _, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
        await SaveSafetyBackupAsync("remote", System.Text.Json.JsonSerializer.Serialize(treeRoots),
            SafetyOperations.ChangePassword, CountBackupNodes(treeRoots)).ConfigureAwait(false);
        var newDerived = _crypto.DeriveKey(newPassword, creds.Id, version);
        await _creds.SetAsync(creds with { DerivedKey = newDerived }).ConfigureAwait(false);
        _log.Info("session", "Password changed, blob re-encrypted");
        await Engine!.PushAsync(ct: ct).ConfigureAwait(false);
        await RotateSafetyMasterBestEffortAsync(currentDerived, newDerived).ConfigureAwait(false);
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
        BackupGuard.ValidateTree(roots);
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
        var (localRoots, _, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
        await SaveSafetyBackupAsync("local", System.Text.Json.JsonSerializer.Serialize(localRoots),
            SafetyOperations.ImportBackup, CountBackupNodes(localRoots)).ConfigureAwait(false);
        await Store.RestoreLocalAsync(roots).ConfigureAwait(false);
        _log.Info("session", $"Imported backup ({count} items)");
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
            if (cur is null)
            {
                continue;
            }

            n++;
            if (cur.Children is not null)
            {
                foreach (var c in cur.Children)
                {
                    if (c is not null)
                    {
                        queue.Enqueue(c);
                    }
                }
            }
        }

        return n;
    }

    /// <summary>Safety backup of a side about to be overwritten (plaintext JSON,
    /// app-private dir). The filename carries UTC time + operation so the user
    /// can pick the right one for rollback; a manifest tracks the rest.</summary>
    public async Task<string> SaveSafetyBackupAsync(
        string side, string plaintextJson, string operation, int items = 0, string? syncId = null)
    {
        await _safetyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SaveSafetyBackupCoreAsync(side, plaintextJson, operation, items, syncId).ConfigureAwait(false);
        }
        finally
        {
            _safetyGate.Release();
        }
    }

    private async Task<string> SaveSafetyBackupCoreAsync(
        string side, string plaintextJson, string operation, int items, string? syncId)
    {
        var dir = SafetyDir();
        Directory.CreateDirectory(dir);
        // Encrypt under the Master Key when it opens with the current link key;
        // otherwise (pre-link, misaligned) store plaintext flagged as such.
        string payload = plaintextJson;
        string? enc = null;
        var master = await LoadSafetyMasterAsync(dir).ConfigureAwait(false);
        var creds = await _creds.GetAsync().ConfigureAwait(false);
        if (master is not null && creds?.DerivedKey is not null &&
            SafetyMaster.FindWrap(master.Wraps, SafetyMaster.Fingerprint(creds.DerivedKey)) is { } wrap)
        {
            try
            {
                var masterB64 = _crypto.Decrypt(wrap.WrappedKey, creds.DerivedKey);
                payload = _crypto.Encrypt(plaintextJson, masterB64);
                enc = "master";
            }
            catch (Exception ex) when (ex is XbsException or FormatException)
            {
                _log.Warn("session", $"Safety master unwrap failed, storing plaintext: {ex.GetType().Name}");
            }
        }

        var now = DateTime.UtcNow;
        var name = SafetyBackupFiles.BuildFileName(now, operation);
        for (int dup = 2; File.Exists(Path.Combine(dir, name)) && dup < 100; dup++)
        {
            name = $"{Path.GetFileNameWithoutExtension(SafetyBackupFiles.BuildFileName(now, operation))}-{dup}.json";
        }

        var path = Path.Combine(dir, name);
        // Atomic payload write: a crash never leaves a half-written backup.
        await AtomicWriteTextAsync(path, payload).ConfigureAwait(false);
        var entries = await ReadSafetyIndexAsync(dir).ConfigureAwait(false);
        entries.RemoveAll(e => e.FileName == name);
        entries.Add(new SafetyBackupEntry(name, now, operation, side, items,
            new FileInfo(path).Length, syncId, enc));
        MergeLegacySafetyBackups(dir, entries);
        // Decide the prune set first, update the index, and only then delete
        // files: a crash can leave an orphan payload, never an index row
        // pointing at a deleted backup.
        var drops = SafetyBackupFiles.Prune(entries);
        entries.RemoveAll(e => drops.Contains(e.FileName));
        await WriteSafetyIndexAsync(dir, entries).ConfigureAwait(false);
        foreach (var drop in drops)
        {
            try { File.Delete(Path.Combine(dir, drop)); } catch { }
        }

        _log.Info("session", $"Safety backup written ({operation}, {side}, {enc ?? "plaintext"})");
        return path;
    }

    /// <summary>Loads (or creates) the safety Master Key record. Null only on
    /// IO failure — callers fall back to plaintext and stay honest about it.</summary>
    private async Task<SafetyMasterRecord?> LoadSafetyMasterAsync(string dir)
    {
        var path = Path.Combine(dir, "safety-master.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var raw = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return System.Text.Json.JsonSerializer.Deserialize<SafetyMasterRecord>(raw);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _log.Warn("session", $"Safety master unreadable: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Atomic master rotation: unwrap with the old KEK, wrap with the
    /// new one, write temp + fsync + rename, then verify by test-unwrap.
    /// The previous file is kept as .prev until the new one verifies.
    /// Already-aligned masters (relink with same credentials) are left alone;
    /// missing/damaged masters are minted fresh (old ciphertext stays listed
    /// but locked, honestly).</summary>
    private async Task RotateSafetyMasterAsync(string? oldKekB64, string newKekB64)
    {
        await _safetyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var dir = SafetyDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "safety-master.json");
            var prev = Path.Combine(dir, "safety-master.prev.json");
            var master = await LoadSafetyMasterAsync(dir).ConfigureAwait(false);
            if (master is not null &&
                SafetyMaster.FindWrap(master.Wraps, SafetyMaster.Fingerprint(newKekB64)) is not null)
            {
                return;
            }

            string masterB64;
            if (master is not null && oldKekB64 is not null &&
                SafetyMaster.FindWrap(master.Wraps, SafetyMaster.Fingerprint(oldKekB64)) is { } oldWrap)
            {
                masterB64 = _crypto.Decrypt(oldWrap.WrappedKey, oldKekB64);
            }
            else
            {
                masterB64 = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                master = new SafetyMasterRecord(SafetyMaster.CurrentFormat, []);
            }

            var newWrap = new SafetyMasterWrap(
                SafetyMaster.Fingerprint(newKekB64), _crypto.Encrypt(masterB64, newKekB64));
            var rotated = new SafetyMasterRecord(
                SafetyMaster.CurrentFormat, SafetyMaster.AddWrap(master.Wraps, newWrap.KekId, newWrap.WrappedKey));
            var raw = System.Text.Json.JsonSerializer.Serialize(rotated);
            var tmp = path + ".tmp";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var sw = new StreamWriter(fs))
            {
                await sw.WriteAsync(raw).ConfigureAwait(false);
                await sw.FlushAsync().ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Copy(path, prev, overwrite: true);
            }

            File.Move(tmp, path, overwrite: true);
            // Verify before trusting it: test-unwrap the fresh record.
            var check = await LoadSafetyMasterAsync(dir).ConfigureAwait(false);
            var verifyWrap = check is not null
                ? SafetyMaster.FindWrap(check.Wraps, SafetyMaster.Fingerprint(newKekB64))
                : null;
            if (verifyWrap is null || _crypto.Decrypt(verifyWrap.WrappedKey, newKekB64) != masterB64)
            {
                throw new InvalidOperationException("Rotated master does not verify.");
            }

            try { File.Delete(prev); } catch { }
            _log.Info("session", "Safety master rotated");
        }
        finally
        {
            _safetyGate.Release();
        }
    }

    /// <summary>Best-effort master rotation after a key change (link, switch,
    /// password change). Rotation must never fail the user flow that already
    /// succeeded — mismatches stay honest and heal on the next rotation.</summary>
    private async Task RotateSafetyMasterBestEffortAsync(string? oldKekB64, string newKekB64)
    {
        try
        {
            await RotateSafetyMasterAsync(oldKekB64, newKekB64).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("session", $"Safety master rotation failed: {ex.GetType().Name}");
        }
    }

    private static async Task AtomicWriteTextAsync(string path, string content)
    {
        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var sw = new StreamWriter(fs))
        {
            await sw.WriteAsync(content).ConfigureAwait(false);
            await sw.FlushAsync().ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>All safety backups, newest first (manifest + legacy files).</summary>
    public Task<List<SafetyBackupEntry>> ListSafetyBackupsAsync(CancellationToken ct = default)
    {
        var dir = SafetyDir();
        if (!Directory.Exists(dir))
        {
            return Task.FromResult(new List<SafetyBackupEntry>());
        }

        var entries = ReadSafetyIndex(dir);
        var known = new HashSet<string>(entries.Select(e => e.FileName), StringComparer.Ordinal);
        foreach (var file in new DirectoryInfo(dir).GetFiles("safety-*.json"))
        {
            if (file.Name == SafetyBackupFiles.IndexFileName || known.Contains(file.Name))
            {
                continue;
            }

            var legacy = SafetyBackupFiles.ParseLegacy(file.Name, file.Length);
            if (legacy is not null)
            {
                entries.Add(legacy);
            }
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult(entries.OrderByDescending(e => e.CreatedAtUtc).ToList());
    }

    /// <summary>Deletes one safety backup (confirmed in UI). Unknown names rejected.</summary>
    public async Task DeleteSafetyBackupAsync(string fileName)
    {
        var path = SafetyPath(fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Backup not found — it may have been pruned.");
        }

        File.Delete(path);
        var dir = SafetyDir();
        var entries = await ReadSafetyIndexAsync(dir).ConfigureAwait(false);
        entries.RemoveAll(e => e.FileName == fileName);
        await WriteSafetyIndexAsync(dir, entries).ConfigureAwait(false);
        _log.Info("session", $"Safety backup deleted ({fileName})");
    }

    /// <summary>Restores a safety backup. Local-side backups replace the local
    /// tree (dirty → pushed or honestly conflicted later); remote-side backups
    /// upload onto the currently linked sync (409 if the server moved). The
    /// overwritten current state is safety-backed-up first.</summary>
    public async Task<int> RestoreSafetyBackupAsync(string fileName, CancellationToken ct = default)
    {
        var entry = (await ListSafetyBackupsAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(e => e.FileName == fileName)
            ?? throw new InvalidOperationException("Backup not found — it may have been pruned.");
        var roots = await OpenSafetyBackupAsync(entry, ct).ConfigureAwait(false);
        var count = BackupGuard.ValidateTree(roots);
        if (entry.Side == "local")
        {
            var (localRoots, _, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
            await SaveSafetyBackupAsync("local", System.Text.Json.JsonSerializer.Serialize(localRoots),
                SafetyOperations.PreRestore, CountBackupNodes(localRoots)).ConfigureAwait(false);
            await Store.RestoreLocalAsync(roots).ConfigureAwait(false);
            _log.Info("session", $"Restored local safety backup ({count} items)");
            return count;
        }

        var creds = await _creds.GetAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Link the sync this backup belongs to first.");
        var (currentRoots, _, _, _) = await Store!.LoadMirrorAsync().ConfigureAwait(false);
        await SaveSafetyBackupAsync("remote", System.Text.Json.JsonSerializer.Serialize(currentRoots),
            SafetyOperations.PreRestore, CountBackupNodes(currentRoots), ShortSyncId(creds.Id)).ConfigureAwait(false);
        // Replace the local tree (dirty) and push: the engine re-encrypts
        // under the current link key with revision CAS. On 409 the mirror
        // still holds the restored tree as dirty, so the next sync surfaces
        // an honest conflict instead of pulling over the restore.
        await Store.RestoreLocalAsync(roots).ConfigureAwait(false);
        await Engine!.PushAsync(ct: ct).ConfigureAwait(false);
        _log.Info("session", $"Restored remote safety backup ({count} items)");
        return count;
    }

    /// <summary>Reads a safety backup, decrypting envelope-encrypted ones with
    /// the current link key. A fingerprint mismatch names the origin sync so
    /// the user can re-link (or supply that password) instead of guessing.</summary>
    private async Task<List<BookmarkNode>> OpenSafetyBackupAsync(SafetyBackupEntry entry, CancellationToken ct)
    {
        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(SafetyPath(entry.FileName), ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Backup file cannot be read.", ex);
        }

        if (entry.Enc == "master")
        {
            var creds = await _creds.GetAsync().ConfigureAwait(false);
            var master = await LoadSafetyMasterAsync(SafetyDir()).ConfigureAwait(false);
            var wrap = creds?.DerivedKey is not null && master is not null
                ? SafetyMaster.FindWrap(master.Wraps, SafetyMaster.Fingerprint(creds.DerivedKey))
                : null;
            if (wrap is null)
            {
                throw new InvalidCredentialsException(
                    $"This backup is locked to another sync{(entry.SyncId is null ? "" : $" ({entry.SyncId})")} — link it (or its password) to open.");
            }

            try
            {
                var masterB64 = _crypto.Decrypt(wrap.WrappedKey, creds!.DerivedKey);
                raw = _crypto.Decrypt(raw, masterB64);
            }
            catch (Exception ex) when (ex is XbsException or FormatException)
            {
                throw new InvalidOperationException("Backup file is damaged.", ex);
            }
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<BookmarkNode>>(raw) ?? [];
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException("Backup file is damaged.", ex);
        }
    }

    /// <summary>Bulk delete with a safety backup first (duplicates cleanup).</summary>
    public async Task<int> DeleteBookmarksAsync(IEnumerable<int> nodeIds, CancellationToken ct = default)
    {
        var ids = nodeIds.Distinct().ToList();
        if (ids.Count == 0 || Store is null || Engine is null)
        {
            return 0;
        }

        var (roots, _, _, _) = await Store.LoadMirrorAsync().ConfigureAwait(false);
        await SaveSafetyBackupAsync("local", System.Text.Json.JsonSerializer.Serialize(roots),
            SafetyOperations.DuplicatesCleanup, CountBackupNodes(roots)).ConfigureAwait(false);
        return await Engine.DeleteBookmarksByNodeIdsAsync(ids, ct).ConfigureAwait(false);
    }

    private static string SafetyDir() =>
        Path.Combine(FileSystem.AppDataDirectory, "safety-backups");

    private static string SafetyPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
            !fileName.StartsWith(SafetyBackupFiles.FilePrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unknown backup.");
        }

        var dir = SafetyDir();
        var full = Path.GetFullPath(Path.Combine(dir, fileName));
        if (full != Path.Combine(dir, fileName))
        {
            throw new InvalidOperationException("Unknown backup.");
        }

        return full;
    }

    private static List<SafetyBackupEntry> ReadSafetyIndex(string dir)
    {
        var index = Path.Combine(dir, SafetyBackupFiles.IndexFileName);
        if (!File.Exists(index))
        {
            return [];
        }

        try
        {
            return SafetyBackupFiles.ParseIndex(File.ReadAllText(index));
        }
        catch (IOException)
        {
            return [];
        }
    }

    private Task<List<SafetyBackupEntry>> ReadSafetyIndexAsync(string dir) =>
        Task.FromResult(ReadSafetyIndex(dir));

    private static Task WriteSafetyIndexAsync(string dir, List<SafetyBackupEntry> entries)
    {
        try
        {
            return AtomicWriteTextAsync(Path.Combine(dir, SafetyBackupFiles.IndexFileName),
                SafetyBackupFiles.SerializeIndex(entries));
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Cannot update the backup index.", ex);
        }
    }

    private static void MergeLegacySafetyBackups(string dir, List<SafetyBackupEntry> entries)
    {
        var known = new HashSet<string>(entries.Select(e => e.FileName), StringComparer.Ordinal);
        foreach (var file in new DirectoryInfo(dir).GetFiles("safety-*.json"))
        {
            if (file.Name == SafetyBackupFiles.IndexFileName || known.Contains(file.Name))
            {
                continue;
            }

            var legacy = SafetyBackupFiles.ParseLegacy(file.Name, file.Length);
            if (legacy is not null)
            {
                entries.Add(legacy);
            }
        }
    }

    private static string? ShortSyncId(string? id) =>
        id is null ? null : id.Length <= 8 ? id : $"{id[..4]}…{id[^2..]}";

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

    /// <summary>Unfinished Switch-service wizard, kept while the user inspects
    /// other pages (e.g. the bookmarks) mid-review. In-memory only, never
    /// persisted: secrets are re-asked on return, the probe (with its derived
    /// key + pre-images) survives navigation. Cleared when the switch
    /// completes or the user starts over.</summary>
    public SwitchDraft? PendingSwitch { get; set; }
}

public sealed record ReconcileInfo(
    SyncCredentials Candidate, int LocalCount, int RemoteCount,
    string? RemoteLastUpdated, string? RemoteVersion,
    string RemoteBlob, string LocalBlob);

/// <summary>Draft of an unfinished Switch-service wizard (see
/// <see cref="XbsSession.PendingSwitch"/>). Secrets are deliberately absent:
/// passwords are always retyped after navigation.</summary>
public sealed record SwitchDraft(
    string TargetUrl, string Slot, string SyncId, bool TakeUpload,
    ReconcileInfo? Probe, string? VerifiedFor, int Step);

public sealed record BackupEnvelope(string App, int Format, string Salt, string Blob);
