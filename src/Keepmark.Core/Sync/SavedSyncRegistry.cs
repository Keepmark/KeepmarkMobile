using Keepmark.Core.Safety;

namespace Keepmark.Core.Sync;

/// <summary>
/// The list of syncs this device knows: the active one plus the dormant rows
/// the user can switch back to. There is one active sync at a time; the active
/// credentials stay where they always were (<see cref="ICredentialStore"/>),
/// so the registry is additive and needs no schema migration.
/// </summary>
public sealed class SavedSyncRegistry(ICredentialStore store, TimeProvider? timeProvider = null)
{
    /// <summary>The list is read-modify-write: without this gate two concurrent
    /// callers (startup migration + link, rename + activation) could drop a row.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private TimeProvider Clock => timeProvider ?? TimeProvider.System;

    public Task<List<SavedSync>> ListAsync() => store.GetSavedSyncsAsync();

    public async Task<SavedSync?> FindAsync(SavedSyncIdentity identity) =>
        SavedSyncs.Find(await store.GetSavedSyncsAsync().ConfigureAwait(false), identity);

    /// <summary>Insert or refresh one row (link/activation time wins).</summary>
    public async Task<SavedSync> RememberAsync(SavedSync row)
    {
        var entry = row with
        {
            ServiceUrl = SavedSyncs.CanonicalServiceUrl(row.ServiceUrl),
            Label = SavedSyncs.NormalizeLabel(row.Label),
            KeyId = row.KeyId ?? Fingerprint(row.DerivedKey),
            LastUsedAt = row.LastUsedAt == default ? Clock.GetUtcNow() : row.LastUsedAt
        };
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var rows = SavedSyncs.Upsert(await store.GetSavedSyncsAsync().ConfigureAwait(false), entry);
            await store.SetSavedSyncsAsync(rows).ConfigureAwait(false);
            return SavedSyncs.Find(rows, entry.Identity) ?? entry;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Keeps the registry in step with the live credentials: on a device that
    /// predates multi-sync this creates the first row (migration), afterwards
    /// it only refreshes the active one. A missing active sync adds it; the
    /// stored last-used time is kept so a restart does not reorder the list.
    /// </summary>
    public async Task<SavedSync?> SyncActiveAsync(SyncCredentials? creds)
    {
        if (creds is null) return null;
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var rows = await store.GetSavedSyncsAsync().ConfigureAwait(false);
            var identity = new SavedSyncIdentity(creds.ServiceUrl, creds.Id);
            var existing = SavedSyncs.Find(rows, identity);
            var entry = new SavedSync
            {
                ServiceUrl = SavedSyncs.CanonicalServiceUrl(creds.ServiceUrl),
                SyncId = creds.Id,
                DerivedKey = creds.DerivedKey,
                SyncVersion = creds.Version,
                KeyId = Fingerprint(creds.DerivedKey),
                LastUsedAt = existing is null || existing.LastUsedAt == default
                    ? Clock.GetUtcNow()
                    : existing.LastUsedAt
            };
            var next = SavedSyncs.Upsert(rows, entry);
            if (!SameRows(rows, next)) await store.SetSavedSyncsAsync(next).ConfigureAwait(false);
            return SavedSyncs.Find(next, entry.Identity);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Forgets a dormant row. Never valid for the active sync: that
    /// one is unlinked through the credentials store.</summary>
    public async Task ForgetAsync(SavedSyncIdentity identity)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var rows = await store.GetSavedSyncsAsync().ConfigureAwait(false);
            await store.SetSavedSyncsAsync(SavedSyncs.Remove(rows, identity)).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Sets (or clears, with null/empty) a row label. Returns false
    /// when another row already answers to that name.</summary>
    public async Task<bool> RenameAsync(SavedSyncIdentity identity, string? label)
    {
        var normalized = SavedSyncs.NormalizeLabel(label);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var rows = await store.GetSavedSyncsAsync().ConfigureAwait(false);
            var current = SavedSyncs.Find(rows, identity);
            if (current is null) return false;
            if (SavedSyncs.LabelTaken(rows, identity, normalized)) return false;
            if ((current.Label ?? string.Empty) == (normalized ?? string.Empty)) return true;

            // Not Upsert: a null label must clear the stored one, not preserve it.
            var key = SavedSyncs.Key(identity.ServiceUrl, identity.SyncId);
            var next = SavedSyncs.Sort(rows.Select(row =>
                SavedSyncs.Key(row.ServiceUrl, row.SyncId) == key ? row with { Label = normalized } : row));
            await store.SetSavedSyncsAsync(next).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string? Fingerprint(string? derivedKey)
    {
        if (string.IsNullOrEmpty(derivedKey)) return null;
        try
        {
            return SafetyMaster.Fingerprint(derivedKey);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool SameRows(List<SavedSync> a, List<SavedSync> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First == pair.Second);
}
