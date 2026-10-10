using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;

namespace Keepmark.Core.Api;

// DTOs mirror the v1.1.13 API contract (bookmarks.service.ts response interfaces).

public sealed class CreateSyncResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    [JsonPropertyName("meta")]
    public SyncMeta? Meta { get; set; }
}

public sealed class GetBookmarksResponse
{
    [JsonPropertyName("bookmarks")]
    public string? Bookmarks { get; set; }
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }
    [JsonPropertyName("meta")]
    public SyncMeta? Meta { get; set; }
}

public sealed class LastUpdatedResponse
{
    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }
}

public sealed class VersionResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class UpdateBookmarksResponse
{
    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }
    [JsonPropertyName("meta")]
    public SyncMeta? Meta { get; set; }
}

/// <summary>v1.2 sync metadata envelope (absent on legacy servers).</summary>
public sealed class SyncMeta
{
    [JsonPropertyName("revision")]
    public long Revision { get; set; }
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }
    [JsonPropertyName("size")]
    public long Size { get; set; }
    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }
}

/// <summary>v1.2 capability advertisement (absent on legacy servers).</summary>
public sealed class ApiCapabilities
{
    [JsonPropertyName("revision")]
    public bool Revision { get; set; }
    [JsonPropertyName("conditionalGet")]
    public bool ConditionalGet { get; set; }
    [JsonPropertyName("delete")]
    public bool Delete { get; set; }

    /// <summary>Raw wire value; kept untyped so a malformed field never fails
    /// the whole probe (that would silently drop the client to v1.1).</summary>
    [JsonPropertyName("syncExpiryDays")]
    public JsonElement? SyncExpiryDaysRaw { get; set; }

    /// <summary>Days an unused sync is kept before the server deletes it
    /// (0 = forever); null when absent or not a non-negative integer.</summary>
    [JsonIgnore]
    public int? SyncExpiryDays =>
        SyncExpiryDaysRaw is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var d) && d >= 0 ? d : null;
}

public sealed class ServiceInfoResponse
{
    [JsonPropertyName("location")]
    public string? Location { get; set; }
    [JsonPropertyName("maxSyncSize")]
    public long MaxSyncSize { get; set; }
    [JsonPropertyName("message")]
    public string? Message { get; set; }
    [JsonPropertyName("status")]
    public ServiceStatus Status { get; set; }
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    [JsonPropertyName("capabilities")]
    public ApiCapabilities? Capabilities { get; set; }
}

public interface IXSyncApiClient
{
    Task<ServiceInfoResponse> GetInfoAsync(CancellationToken ct = default);
    /// <summary>v1.2 info probe: same route, 1.2.0 contract. Legacy servers
    /// answer 412 (mapped to <see cref="Errors.ServerPreconditionException"/>) or omit capabilities.</summary>
    Task<ServiceInfoResponse> GetInfoV12Async(CancellationToken ct = default);
    Task<CreateSyncResponse> CreateSyncAsync(CancellationToken ct = default);
    Task<GetBookmarksResponse> GetBookmarksAsync(string syncId, CancellationToken ct = default);
    /// <summary>v1.2 full GET: same shape plus the <c>meta</c> envelope (revision/hash).</summary>
    Task<GetBookmarksResponse> GetBookmarksV12Async(string syncId, CancellationToken ct = default);
    /// <summary>v1.2 conditional GET: null when the server answers 304.</summary>
    Task<GetBookmarksResponse?> GetBookmarksIfChangedAsync(string syncId, string knownHash, CancellationToken ct = default);
    Task<LastUpdatedResponse> GetLastUpdatedAsync(string syncId, CancellationToken ct = default);
    Task<VersionResponse> GetVersionAsync(string syncId, CancellationToken ct = default);
    Task<UpdateBookmarksResponse> UpdateBookmarksAsync(
        string syncId, string encryptedBookmarks, string lastUpdated, bool updateVersion = false, CancellationToken ct = default);
    /// <summary>v1.2 revision-based update (atomic CAS on revision).</summary>
    Task<UpdateBookmarksResponse> UpdateBookmarksV12Async(
        string syncId, string encryptedBookmarks, long baseRevision, bool updateVersion = false, CancellationToken ct = default);
}

/// <summary>
/// HTTP client for the xBrowserSync API v1.1.13.
/// One instance per service URL (HttpClient is injected). Every call logs
/// START/END with timing at Info; bodies are never logged.
/// </summary>
public sealed class XSyncApiClient : IXSyncApiClient, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly IXbsLocalizer _str;
    private readonly IXbsLogger _log;

    public XSyncApiClient(HttpClient http, IXbsLocalizer str, IXbsLogger log)
    {
        _http = http;
        _str = str;
        _log = log;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Builds a client bound to a service URL. The Accept-Version header
    /// is set per request (SendAsync), defaulting to ApiVersion (1.1.x contract).</summary>
    public static XSyncApiClient ForService(string serviceUrl, IXbsLocalizer str, IXbsLogger log)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(serviceUrl.TrimEnd('/') + '/'),
            Timeout = XbsConstants.DefaultHttpTimeout
        };
        return new XSyncApiClient(http, str, log);
    }

    public Task<ServiceInfoResponse> GetInfoAsync(CancellationToken ct = default) =>
        SendAsync<ServiceInfoResponse>(HttpMethod.Get, "info", null, static r => r.Status != 0 && r.Version is not null, ct);

    public Task<ServiceInfoResponse> GetInfoV12Async(CancellationToken ct = default) =>
        SendAsync<ServiceInfoResponse>(HttpMethod.Get, "info", null, static r => r.Status != 0 && r.Version is not null, ct,
            apiVersion: XbsConstants.ApiVersion12);

    public async Task<CreateSyncResponse> CreateSyncAsync(CancellationToken ct = default)
    {
        var body = new { version = XbsConstants.SyncClientVersion };
        var res = await SendAsync<CreateSyncResponse>(HttpMethod.Post, "bookmarks", body,
            static r => r.Id is not null && r.LastUpdated is not null && r.Version is not null, ct,
            idempotent: false);
        return res;
    }

    public Task<GetBookmarksResponse> GetBookmarksAsync(string syncId, CancellationToken ct = default) =>
        SendAsync<GetBookmarksResponse>(HttpMethod.Get, $"bookmarks/{syncId}", null,
            static r => r.LastUpdated is not null, ct, syncId);

    public Task<GetBookmarksResponse> GetBookmarksV12Async(string syncId, CancellationToken ct = default) =>
        SendAsync<GetBookmarksResponse>(HttpMethod.Get, $"bookmarks/{syncId}", null,
            static r => r.LastUpdated is not null, ct, syncId, XbsConstants.ApiVersion12);

    public Task<LastUpdatedResponse> GetLastUpdatedAsync(string syncId, CancellationToken ct = default) =>
        SendAsync<LastUpdatedResponse>(HttpMethod.Get, $"bookmarks/{syncId}/lastUpdated", null,
            static r => r.LastUpdated is not null, ct, syncId);

    public Task<VersionResponse> GetVersionAsync(string syncId, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Get, $"bookmarks/{syncId}/version", null,
            static r => r.Version is not null, ct, syncId);

    public Task<UpdateBookmarksResponse> UpdateBookmarksAsync(
        string syncId, string encryptedBookmarks, string lastUpdated, bool updateVersion = false, CancellationToken ct = default)
    {
        object body = updateVersion
            ? new { bookmarks = encryptedBookmarks, lastUpdated, version = XbsConstants.SyncClientVersion }
            : new { bookmarks = encryptedBookmarks, lastUpdated };
        return SendAsync<UpdateBookmarksResponse>(HttpMethod.Put, $"bookmarks/{syncId}", body,
            static r => r.LastUpdated is not null, ct, syncId, idempotent: false);
    }

    public Task<UpdateBookmarksResponse> UpdateBookmarksV12Async(
        string syncId, string encryptedBookmarks, long baseRevision, bool updateVersion = false, CancellationToken ct = default)
    {
        object body = updateVersion
            ? new { bookmarks = encryptedBookmarks, baseRevision, version = XbsConstants.SyncClientVersion }
            : new { bookmarks = encryptedBookmarks, baseRevision };
        return SendAsync<UpdateBookmarksResponse>(HttpMethod.Put, $"bookmarks/{syncId}", body,
            static r => r.LastUpdated is not null, ct, syncId, XbsConstants.ApiVersion12, idempotent: false);
    }

    public Task<GetBookmarksResponse?> GetBookmarksIfChangedAsync(
        string syncId, string knownHash, CancellationToken ct = default)
        => WithRetryAsync(
            () => AttemptConditionalAsync(syncId, knownHash, ct),
            $"GET bookmarks/{{id}}", syncId, idempotent: true, ct);

    private async Task<GetBookmarksResponse?> AttemptConditionalAsync(
        string syncId, string knownHash, CancellationToken ct)
    {
        var label = LogSanitizer.PathFor($"GET bookmarks/{{id}}", syncId);
        var sw = Stopwatch.StartNew();
        _log.Info("net", $"→ START {label} (If-None-Match)");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"bookmarks/{syncId}");
            req.Headers.TryAddWithoutValidation("Accept-Version", XbsConstants.ApiVersion12);
            req.Headers.TryAddWithoutValidation("If-None-Match", $"\"{knownHash}\"");
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            sw.Stop();
            _log.Info("net", $"← END {label} {(int)res.StatusCode} in {sw.ElapsedMilliseconds}ms");
            if (res.StatusCode == HttpStatusCode.NotModified)
            {
                return null;
            }

            if (!res.IsSuccessStatusCode)
            {
                throw MapError(res.StatusCode, await ReadMessageAsync(res, ct).ConfigureAwait(false), res);
            }

            var dto = await res.Content.ReadFromJsonAsync<GetBookmarksResponse>(Json, ct).ConfigureAwait(false);
            if (dto is null || dto.LastUpdated is null)
            {
                throw new UnexpectedResponseException(_str.Get(Str.ErrUnexpectedResponse));
            }

            return dto;
        }
        catch (XbsException)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _log.Warn("net", $"← TIMEOUT {label} after {sw.ElapsedMilliseconds}ms");
            throw new ServiceOfflineException(_str.Get(Str.ErrServiceOffline), ex);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _log.Warn("net", $"← FAIL {label} after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}");
            throw new ServiceOfflineException(_str.Get(Str.ErrServiceOffline), ex);
        }
    }

    private Task<T> SendAsync<T>(
        HttpMethod method, string path, object? body, Func<T, bool> valid, CancellationToken ct,
        string? syncId = null, string? apiVersion = null, bool idempotent = true)
        => WithRetryAsync(
            () => AttemptAsync<T>(method, path, body, valid, apiVersion, syncId, ct),
            $"{method} {path}", syncId, idempotent, ct);

    /// <summary>Retry loop: transient failures are retried with backoff, user
    /// cancellation and non-transient errors propagate immediately.
    /// Non-idempotent calls (sync creation, bookmark updates) run exactly once.</summary>
    private async Task<T> WithRetryAsync<T>(
        Func<Task<T>> attempt, string route, string? syncId, bool idempotent, CancellationToken ct)
    {
        var done = 0;
        while (true)
        {
            done++;
            try
            {
                return await attempt().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is XbsException or HttpRequestException or TaskCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                var retryAfter = ex is ThrottledException th && th.RetryAfterSeconds.HasValue
                    ? TimeSpan.FromSeconds(th.RetryAfterSeconds.Value)
                    : (TimeSpan?)null;
                var (retry, delay) = Resilience.RetryPolicy.ForAttempt(ex, done, retryAfter);
                if (!idempotent || !retry)
                {
                    throw;
                }

                _log.Warn("net", $"↻ RETRY {LogSanitizer.PathFor(route, syncId)} attempt {done} in {(int)delay.TotalMilliseconds}ms ({ex.GetType().Name})");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<T> AttemptAsync<T>(
        HttpMethod method, string path, object? body, Func<T, bool> valid,
        string? apiVersion, string? syncId, CancellationToken ct)
    {
        var label = LogSanitizer.PathFor($"{method} {path}", syncId);
        var sw = Stopwatch.StartNew();
        _log.Info("net", $"→ START {label}");
        try
        {
            using var req = new HttpRequestMessage(method, path);
            req.Headers.TryAddWithoutValidation("Accept-Version", apiVersion ?? XbsConstants.ApiVersion);
            if (body is not null)
            {
                req.Content = JsonContent.Create(body);
            }

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var bytes = res.Content.Headers.ContentLength;
            sw.Stop();
            _log.Info("net", $"← END {label} {(int)res.StatusCode} in {sw.ElapsedMilliseconds}ms" +
                (bytes.HasValue ? $", {bytes.Value}B" : string.Empty));

            if (!res.IsSuccessStatusCode)
            {
                throw MapError(res.StatusCode, await ReadMessageAsync(res, ct).ConfigureAwait(false), res);
            }

            var dto = await res.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
            if (dto is null || !valid(dto))
            {
                throw new UnexpectedResponseException(_str.Get(Str.ErrUnexpectedResponse));
            }

            return dto;
        }
        catch (XbsException)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _log.Warn("net", $"← TIMEOUT {label} after {sw.ElapsedMilliseconds}ms");
            throw new ServiceOfflineException(_str.Get(Str.ErrServiceOffline), ex);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _log.Warn("net", $"← FAIL {label} after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}");
            throw new ServiceOfflineException(_str.Get(Str.ErrServiceOffline), ex);
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            using var doc = await res.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
            if (doc is not null && doc.RootElement.TryGetProperty("message", out var msg))
            {
                return msg.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Non-JSON error body: fall through to the default message.
        }

        return string.Empty;
    }

    private XbsException MapError(HttpStatusCode status, string serverMessage, HttpResponseMessage? res = null) => status switch
    {
        HttpStatusCode.Unauthorized => new SyncNotFoundException(WithServer(_str.Get(Str.ErrSyncNotFound), serverMessage)),
        HttpStatusCode.NotFound => new InvalidServiceException(WithServer(_str.Get(Str.ErrInvalidService), serverMessage)),
        HttpStatusCode.MethodNotAllowed => new NotAcceptingNewSyncsException(WithServer(_str.Get(Str.ErrNotAcceptingNewSyncs), serverMessage)),
        HttpStatusCode.NotAcceptable => new DailyNewSyncLimitException(WithServer(_str.Get(Str.ErrDailyNewSyncLimit), serverMessage)),
        HttpStatusCode.Conflict => new DataOutOfSyncException(WithServer(_str.Get(Str.ErrDataOutOfSync), serverMessage)),
        HttpStatusCode.PreconditionFailed => new ServerPreconditionException(WithServer(Loc.Get(Str.ErrPreconditionContract), serverMessage)),
        HttpStatusCode.RequestEntityTooLarge => new SyncTooLargeException(WithServer(_str.Get(Str.ErrSyncTooLarge), serverMessage)),
        HttpStatusCode.TooManyRequests => new ThrottledException(WithServer(_str.Get(Str.ErrThrottled), serverMessage))
        {
            RetryAfterSeconds = ReadRetryAfter(res),
        },
        HttpStatusCode.BadRequest => new UnexpectedResponseException(WithServer(_str.Get(Str.ErrUnexpectedResponse), serverMessage)),
        >= HttpStatusCode.InternalServerError => new ServiceOfflineException(WithServer(_str.Get(Str.ErrServiceOffline), serverMessage)),
        _ => new ServiceOfflineException(WithServer(_str.Get(Str.ErrServiceOffline), serverMessage)),
    };

    private static int? ReadRetryAfter(HttpResponseMessage? res)
    {
        if (res?.Headers.RetryAfter is null)
        {
            return null;
        }

        if (res.Headers.RetryAfter.Delta is TimeSpan delta)
        {
            return (int)delta.TotalSeconds;
        }

        if (res.Headers.RetryAfter.Date is DateTimeOffset date)
        {
            var secs = (int)(date - DateTimeOffset.UtcNow).TotalSeconds;
            return secs < 0 ? 0 : secs;
        }

        return null;
    }

    private static string WithServer(string local, string server) =>
        string.IsNullOrWhiteSpace(server) ? local : $"{local} (server: {server})";
}
