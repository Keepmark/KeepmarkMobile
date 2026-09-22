using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using XBrowserSyncNg.Core.Errors;
using XBrowserSyncNg.Core.I18n;
using XBrowserSyncNg.Core.Logging;
using XBrowserSyncNg.Core.Models;

namespace XBrowserSyncNg.Core.Api;

// DTOs mirror the v1.1.13 API contract (bookmarks.service.ts response interfaces).

public sealed class CreateSyncResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class GetBookmarksResponse
{
    [JsonPropertyName("bookmarks")]
    public string? Bookmarks { get; set; }
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }
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
}

public interface IXSyncApiClient
{
    Task<ServiceInfoResponse> GetInfoAsync(CancellationToken ct = default);
    Task<CreateSyncResponse> CreateSyncAsync(CancellationToken ct = default);
    Task<GetBookmarksResponse> GetBookmarksAsync(string syncId, CancellationToken ct = default);
    Task<LastUpdatedResponse> GetLastUpdatedAsync(string syncId, CancellationToken ct = default);
    Task<VersionResponse> GetVersionAsync(string syncId, CancellationToken ct = default);
    Task<UpdateBookmarksResponse> UpdateBookmarksAsync(
        string syncId, string encryptedBookmarks, string lastUpdated, bool updateVersion = false, CancellationToken ct = default);
}

/// <summary>
/// HTTP client for the xBrowserSync API v1.1.13.
/// One instance per service URL (HttpClient is injected). Every call logs
/// START/END with timing at Info; bodies are never logged.
/// </summary>
public sealed class XSyncApiClient : IXSyncApiClient
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

    /// <summary>Builds a client bound to a service URL with the required Accept-Version header.</summary>
    public static XSyncApiClient ForService(string serviceUrl, IXbsLocalizer str, IXbsLogger log)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(serviceUrl.TrimEnd('/') + '/'),
            Timeout = XbsConstants.DefaultHttpTimeout
        };
        http.DefaultRequestHeaders.Add("Accept-Version", XbsConstants.ApiVersion);
        return new XSyncApiClient(http, str, log);
    }

    public Task<ServiceInfoResponse> GetInfoAsync(CancellationToken ct = default) =>
        SendAsync<ServiceInfoResponse>(HttpMethod.Get, "info", null, static r => r.Status != 0 && r.Version is not null, ct);

    public async Task<CreateSyncResponse> CreateSyncAsync(CancellationToken ct = default)
    {
        var body = new { version = XbsConstants.SyncClientVersion };
        var res = await SendAsync<CreateSyncResponse>(HttpMethod.Post, "bookmarks", body,
            static r => r.Id is not null && r.LastUpdated is not null && r.Version is not null, ct);
        return res;
    }

    public Task<GetBookmarksResponse> GetBookmarksAsync(string syncId, CancellationToken ct = default) =>
        SendAsync<GetBookmarksResponse>(HttpMethod.Get, $"bookmarks/{syncId}", null,
            static r => r.LastUpdated is not null, ct, syncId);

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
            static r => r.LastUpdated is not null, ct, syncId);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, object? body, Func<T, bool> valid, CancellationToken ct, string? syncId = null)
    {
        var label = LogSanitizer.PathFor($"{method} {path}", syncId);
        var sw = Stopwatch.StartNew();
        _log.Info("net", $"→ START {label}");
        try
        {
            using var req = new HttpRequestMessage(method, path);
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
                throw MapError(res.StatusCode, await ReadMessageAsync(res, ct).ConfigureAwait(false));
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

    private XbsException MapError(HttpStatusCode status, string serverMessage) => status switch
    {
        HttpStatusCode.Unauthorized => new SyncNotFoundException(WithServer(_str.Get(Str.ErrSyncNotFound), serverMessage)),
        HttpStatusCode.NotFound => new InvalidServiceException(WithServer(_str.Get(Str.ErrInvalidService), serverMessage)),
        HttpStatusCode.MethodNotAllowed => new NotAcceptingNewSyncsException(WithServer(_str.Get(Str.ErrNotAcceptingNewSyncs), serverMessage)),
        HttpStatusCode.NotAcceptable => new DailyNewSyncLimitException(WithServer(_str.Get(Str.ErrDailyNewSyncLimit), serverMessage)),
        HttpStatusCode.Conflict => new DataOutOfSyncException(WithServer(_str.Get(Str.ErrDataOutOfSync), serverMessage)),
        HttpStatusCode.RequestEntityTooLarge => new SyncTooLargeException(WithServer(_str.Get(Str.ErrSyncTooLarge, 0), serverMessage)),
        HttpStatusCode.TooManyRequests => new ThrottledException(WithServer(_str.Get(Str.ErrThrottled), serverMessage)),
        >= HttpStatusCode.InternalServerError => new ServiceOfflineException(WithServer(_str.Get(Str.ErrServiceOffline), serverMessage)),
        _ => new ServiceOfflineException(WithServer(_str.Get(Str.ErrServiceOffline), serverMessage)),
    };

    private static string WithServer(string local, string server) =>
        string.IsNullOrWhiteSpace(server) ? local : $"{local} (server: {server})";
}
