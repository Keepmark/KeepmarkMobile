namespace Keepmark.Core.Errors;

// Typed failures mapped from HTTP status codes.
// Mapping mirrors getErrorFromHttpResponse in api-xbrowsersync.service.ts.

public class XbsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>401 — sync ID not found on the service.</summary>
public sealed class SyncNotFoundException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>404 — not a valid xBrowserSync service.</summary>
public sealed class InvalidServiceException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>405 — service is not accepting new syncs.</summary>
public sealed class NotAcceptingNewSyncsException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>406 — daily new-sync limit reached.</summary>
public sealed class DailyNewSyncLimitException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>409 — remote changed since our lastUpdated; re-pull before pushing.</summary>
public sealed class DataOutOfSyncException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>413 — encrypted blob exceeds the service maxSyncSize.</summary>
public sealed class SyncTooLargeException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>429 — throttled by the service.</summary>
public sealed class ThrottledException(string message, Exception? inner = null) : XbsException(message, inner)
{
    /// <summary>Seconds from the service Retry-After header, if present.</summary>
    public int? RetryAfterSeconds { get; init; }
}

/// <summary>5xx or unreachable — service considered offline.</summary>
public sealed class ServiceOfflineException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>Decryption failed — almost always a wrong password for the sync ID.</summary>
public sealed class InvalidCredentialsException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>Remote sync format is newer than <see cref="XbsConstants.SyncClientVersion"/>.</summary>
public sealed class UnsupportedSyncVersionException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>Service version is older than <see cref="XbsConstants.MinApiVersion"/>.</summary>
public sealed class UnsupportedApiVersionException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>412 — server does not honor the requested contract version
/// (e.g. a legacy server answering a 1.2.0 probe). Never retried; the
/// caller falls back to the legacy contract.</summary>
public sealed class ServerPreconditionException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>Sync ID or derived key missing from local storage.</summary>
public sealed class IncompleteSyncInfoException(string message, Exception? inner = null) : XbsException(message, inner);

/// <summary>Response shape is not what the API contract promises.</summary>
public sealed class UnexpectedResponseException(string message, Exception? inner = null) : XbsException(message, inner);
