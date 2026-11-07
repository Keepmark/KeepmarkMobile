using Keepmark.Core.Sync;

namespace KeepmarkMobile.Services;

// Secrets in platform SecureStorage (Android: EncryptedSharedPreferences/KeyStore).
// Stored: sync ID, service URL, DERIVED key (base64, never the raw password),
// sync version, and the 64-hex database key generated once at first run.

public sealed class SecureCredentialStore : ICredentialStore
{
    private const string KId = "xbs_sync_id";
    private const string KUrl = "xbs_service_url";
    private const string KKey = "xbs_derived_key";
    private const string KVer = "xbs_sync_version";
    private const string KDbKey = "xbs_db_key";
    private const string KSavedSyncs = "xbs_saved_syncs";

    // Read-through cache. MAUI's Android SecureStorage rebuilds
    // EncryptedSharedPreferences (Tink keyset + Keystore unwrap) on EVERY call,
    // ~100-300 ms each; start-up read the same keys ~10 times in sequence
    // (1.3 s in Home alone). Each key now hits the platform store once per
    // process. All writes go through this class, so write-through keeps the
    // cache exact; the gate stops a slow miss from caching a value that a
    // concurrent write already replaced. The values were already held in
    // memory by the session (credentials, DB key), so exposure is unchanged.
    private readonly Dictionary<string, string?> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<string?> ReadAsync(string key)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
            var value = await SecureStorage.GetAsync(key).ConfigureAwait(false);
            _cache[key] = value;
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteAsync(string key, string value)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Drop first: if the platform write fails, the next read goes
            // back to the store instead of trusting a stale cached value.
            _cache.Remove(key);
            await SecureStorage.SetAsync(key, value).ConfigureAwait(false);
            _cache[key] = value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RemoveAsync(string key)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _cache.Remove(key);
            SecureStorage.Remove(key);
            _cache[key] = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SyncCredentials?> GetAsync()
    {
        var id = await ReadAsync(KId).ConfigureAwait(false);
        var key = await ReadAsync(KKey).ConfigureAwait(false);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key))
        {
            return null;
        }

        return new SyncCredentials(
            id,
            await ReadAsync(KUrl).ConfigureAwait(false) ?? Core.XbsConstants.DefaultServiceUrl,
            key,
            await ReadAsync(KVer).ConfigureAwait(false));
    }

    public async Task SetAsync(SyncCredentials creds)
    {
        await WriteAsync(KId, creds.Id).ConfigureAwait(false);
        await WriteAsync(KUrl, creds.ServiceUrl).ConfigureAwait(false);
        await WriteAsync(KKey, creds.DerivedKey).ConfigureAwait(false);
        if (creds.Version is null)
        {
            await RemoveAsync(KVer).ConfigureAwait(false);
        }
        else
        {
            await WriteAsync(KVer, creds.Version).ConfigureAwait(false);
        }
    }

    public async Task ClearAsync()
    {
        await RemoveAsync(KId).ConfigureAwait(false);
        await RemoveAsync(KUrl).ConfigureAwait(false);
        await RemoveAsync(KKey).ConfigureAwait(false);
        await RemoveAsync(KVer).ConfigureAwait(false);
    }

    // Dormant syncs: JSON in the same encrypted store as the active secrets.
    // Sanitized on read (old builds and hand-edited state are not trusted).
    public async Task<List<SavedSync>> GetSavedSyncsAsync()
    {
        var json = await ReadAsync(KSavedSyncs).ConfigureAwait(false);
        return SavedSyncs.Deserialize(json);
    }

    public async Task SetSavedSyncsAsync(IReadOnlyList<SavedSync> rows)
    {
        if (rows.Count == 0)
        {
            await RemoveAsync(KSavedSyncs).ConfigureAwait(false);
            return;
        }

        await WriteAsync(KSavedSyncs, SavedSyncs.Serialize(rows)).ConfigureAwait(false);
    }

    public async Task<string> GetOrCreateDbKeyAsync()
    {
        var existing = await ReadAsync(KDbKey).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existing) && existing.Length == 64)
        {
            return existing;
        }

        var fresh = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await WriteAsync(KDbKey, fresh).ConfigureAwait(false);
        return fresh;
    }
}
