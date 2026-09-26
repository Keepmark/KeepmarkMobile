namespace Keepmark.Core.I18n;

// English-only for now (plan decision). Every user-facing and log string goes
// through keys here so a future multilingual pass only adds a table.
// Log strings stay English forever, even after UI localization.

public interface IXbsLocalizer
{
    string Get(string key, params object?[] args);
}

public static class Str
{
    public const string ErrSyncNotFound = nameof(ErrSyncNotFound);
    public const string ErrInvalidService = nameof(ErrInvalidService);
    public const string ErrNotAcceptingNewSyncs = nameof(ErrNotAcceptingNewSyncs);
    public const string ErrDailyNewSyncLimit = nameof(ErrDailyNewSyncLimit);
    public const string ErrDataOutOfSync = nameof(ErrDataOutOfSync);
    public const string ErrSyncTooLarge = nameof(ErrSyncTooLarge);
    public const string ErrThrottled = nameof(ErrThrottled);
    public const string ErrServiceOffline = nameof(ErrServiceOffline);
    public const string ErrInvalidCredentials = nameof(ErrInvalidCredentials);
    public const string ErrUnsupportedSyncVersion = nameof(ErrUnsupportedSyncVersion);
    public const string ErrUnsupportedApiVersion = nameof(ErrUnsupportedApiVersion);
    public const string ErrIncompleteSyncInfo = nameof(ErrIncompleteSyncInfo);
    public const string ErrUnexpectedResponse = nameof(ErrUnexpectedResponse);
    public const string ErrNetwork = nameof(ErrNetwork);
}

public sealed class EnglishLocalizer : IXbsLocalizer
{
    private static readonly Dictionary<string, string> Table = new()
    {
        [Str.ErrSyncNotFound] = "Sync not found on this service. It may have been removed (syncs expire after 3 weeks of inactivity).",
        [Str.ErrInvalidService] = "This does not look like a valid xBrowserSync service. Check the service URL.",
        [Str.ErrNotAcceptingNewSyncs] = "This service is not accepting new syncs right now. You can still connect an existing sync ID.",
        [Str.ErrDailyNewSyncLimit] = "Daily new-sync limit reached on this service. Try again tomorrow.",
        [Str.ErrDataOutOfSync] = "The server copy changed since your last sync. Pull the latest version before uploading.",
        [Str.ErrSyncTooLarge] = "Sync data exceeds the service size limit. Remove some bookmarks and retry.",
        [Str.ErrThrottled] = "Too many requests. The service asked us to slow down — retry in a few minutes.",
        [Str.ErrServiceOffline] = "Cannot reach the service. Check your connection and the service URL.",
        [Str.ErrInvalidCredentials] = "Wrong password for this sync ID. Decryption failed.",
        [Str.ErrUnsupportedSyncVersion] = "This sync was created by a newer client (v{0}). Update the app to open it.",
        [Str.ErrUnsupportedApiVersion] = "Service API v{0} is older than the minimum supported v{1}.",
        [Str.ErrIncompleteSyncInfo] = "Sync is not set up yet. Connect to a service first.",
        [Str.ErrUnexpectedResponse] = "The service returned data in an unexpected format.",
        [Str.ErrNetwork] = "Network error: {0}",
    };

    public string Get(string key, params object?[] args)
    {
        if (!Table.TryGetValue(key, out var template))
        {
            return key;
        }

        return args.Length == 0 ? template : string.Format(template, args);
    }
}
