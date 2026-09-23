using Keepmark.Core.Sync;

namespace Keepmark.Services;

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

    public async Task<SyncCredentials?> GetAsync()
    {
        var id = await SecureStorage.GetAsync(KId).ConfigureAwait(false);
        var key = await SecureStorage.GetAsync(KKey).ConfigureAwait(false);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key))
        {
            return null;
        }

        return new SyncCredentials(
            id,
            await SecureStorage.GetAsync(KUrl).ConfigureAwait(false) ?? Core.XbsConstants.DefaultServiceUrl,
            key,
            await SecureStorage.GetAsync(KVer).ConfigureAwait(false));
    }

    public async Task SetAsync(SyncCredentials creds)
    {
        await SecureStorage.SetAsync(KId, creds.Id).ConfigureAwait(false);
        await SecureStorage.SetAsync(KUrl, creds.ServiceUrl).ConfigureAwait(false);
        await SecureStorage.SetAsync(KKey, creds.DerivedKey).ConfigureAwait(false);
        if (creds.Version is null)
        {
            SecureStorage.Remove(KVer);
        }
        else
        {
            await SecureStorage.SetAsync(KVer, creds.Version).ConfigureAwait(false);
        }
    }

    public Task ClearAsync()
    {
        SecureStorage.Remove(KId);
        SecureStorage.Remove(KUrl);
        SecureStorage.Remove(KKey);
        SecureStorage.Remove(KVer);
        return Task.CompletedTask;
    }

    public async Task<string> GetOrCreateDbKeyAsync()
    {
        var existing = await SecureStorage.GetAsync(KDbKey).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existing) && existing.Length == 64)
        {
            return existing;
        }

        var fresh = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await SecureStorage.SetAsync(KDbKey, fresh).ConfigureAwait(false);
        return fresh;
    }
}
