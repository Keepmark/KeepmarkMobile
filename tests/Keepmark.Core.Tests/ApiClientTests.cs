using System.Net;
using System.Text;
using Keepmark.Core.Api;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;

namespace Keepmark.Core.Tests;

sealed class FakeHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(Responder(request));
    }
}

public sealed class ApiClientTests
{
    private static (XSyncApiClient client, FakeHandler handler, InMemoryLogger log) Create()
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var log = new InMemoryLogger();
        return (new XSyncApiClient(http, new XbsLocalizer(), log), handler, log);
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    [Fact]
    public async Task GetInfo_ReturnsServiceInfo_AndLogsStartEnd()
    {
        var (client, handler, log) = Create();
        handler.Responder = _ => Json(new { location = "GB", maxSyncSize = 1048576, message = "hi", status = 1, version = "1.1.13" });

        var info = await client.GetInfoAsync();

        Assert.Equal("1.1.13", info.Version);
        Assert.Equal(1048576, info.MaxSyncSize);
        Assert.Contains(log.Entries, e => e.Message.Contains("START GET info"));
        Assert.Contains(log.Entries, e => e.Message.Contains("END GET info") && e.Message.Contains("200"));
    }

    [Fact]
    public async Task GetBookmarks_RedactsSyncId_InLogs()
    {
        var (client, handler, log) = Create();
        var syncId = "12345678-1234-1234-1234-123456789abc";
        handler.Responder = _ => Json(new { bookmarks = "blob", version = "1.8.0", lastUpdated = "2024-01-01T00:00:00.000Z" });

        await client.GetBookmarksAsync(syncId);

        Assert.DoesNotContain(log.Entries, e => e.Message.Contains(syncId));
        Assert.Contains(log.Entries, e => e.Message.Contains("1234…bc"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(SyncNotFoundException))]
    [InlineData(HttpStatusCode.NotFound, typeof(InvalidServiceException))]
    [InlineData(HttpStatusCode.MethodNotAllowed, typeof(NotAcceptingNewSyncsException))]
    [InlineData(HttpStatusCode.NotAcceptable, typeof(DailyNewSyncLimitException))]
    [InlineData(HttpStatusCode.Conflict, typeof(DataOutOfSyncException))]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, typeof(SyncTooLargeException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(ThrottledException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(ServiceOfflineException))]
    public async Task ErrorStatus_MapsToTypedException(HttpStatusCode status, Type expected)
    {
        var (client, handler, _) = Create();
        handler.Responder = _ => Json(new { message = "srv says no" }, status);

        var ex = await Assert.ThrowsAsync(expected, () => client.GetLastUpdatedAsync("some-id"));
        Assert.Contains("srv says no", ex.Message);
    }

    [Fact]
    public async Task InvalidShape_ThrowsUnexpectedResponse()
    {
        var (client, handler, _) = Create();
        handler.Responder = _ => Json(new { nope = 1 });

        await Assert.ThrowsAsync<UnexpectedResponseException>(() => client.GetInfoAsync());
    }

    [Fact]
    public async Task CreateSync_SendsClientVersion()
    {
        var (client, handler, _) = Create();
        string? sent = null;
        handler.Responder = req =>
        {
            sent = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(new { id = "x", lastUpdated = "t", version = "1.8.0" });
        };

        await client.CreateSyncAsync();

        Assert.Contains(XbsConstants.SyncClientVersion, sent);
    }

    [Fact]
    public async Task Requests_CarryVersionHeaderPerCall()
    {
        var (client, handler, _) = Create();
        var versions = new List<string>();
        handler.Responder = req =>
        {
            versions.Add(req.Headers.GetValues("Accept-Version").Single());
            return Json(new { lastUpdated = "t" });
        };

        await client.GetLastUpdatedAsync("id");
        await client.UpdateBookmarksV12Async("id", "blob", 3);

        Assert.Equal(new[] { XbsConstants.ApiVersion, XbsConstants.ApiVersion12 }, versions);
    }

    [Fact]
    public async Task ConditionalGet_ReturnsNullOn304()
    {
        var (client, handler, _) = Create();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NotModified);

        Assert.Null(await client.GetBookmarksIfChangedAsync("id", "hash"));
    }

    [Fact]
    public async Task TransientFailure_RetriedThenSucceeds()
    {
        var (client, handler, _) = Create();
        var calls = 0;
        handler.Responder = _ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpRequestException("flaky");
            }

            return Json(new { lastUpdated = "t" });
        };

        var res = await client.GetLastUpdatedAsync("id");

        Assert.Equal("t", res.LastUpdated);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PersistentFailure_StopsAfterMaxAttempts()
    {
        var (client, handler, _) = Create();
        var calls = 0;
        handler.Responder = _ =>
        {
            calls++;
            throw new HttpRequestException("down");
        };

        await Assert.ThrowsAsync<ServiceOfflineException>(() => client.GetLastUpdatedAsync("id"));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Create_NeverRetried()
    {
        var (client, handler, _) = Create();
        var calls = 0;
        handler.Responder = _ =>
        {
            calls++;
            return Json(new { message = "boom" }, HttpStatusCode.InternalServerError);
        };

        await Assert.ThrowsAsync<ServiceOfflineException>(() => client.CreateSyncAsync());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Throttled_CarriesRetryAfter_AndRetries()
    {
        var (client, handler, _) = Create();
        var calls = 0;
        handler.Responder = _ =>
        {
            calls++;
            if (calls <= 2)
            {
                var r = Json(new { message = "slow" }, HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return r;
            }

            return Json(new { lastUpdated = "t" });
        };

        var res = await client.GetLastUpdatedAsync("id");

        Assert.Equal("t", res.LastUpdated);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task NonTransient_NeverRetried()
    {
        var (client, handler, _) = Create();
        var calls = 0;
        handler.Responder = _ =>
        {
            calls++;
            return Json(new { message = "no" }, HttpStatusCode.Unauthorized);
        };

        await Assert.ThrowsAsync<SyncNotFoundException>(() => client.GetLastUpdatedAsync("id"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Update_LegacyAndV12_NeverRetried()
    {
        foreach (var v12 in new[] { false, true })
        {
            var (client, handler, _) = Create();
            var calls = 0;
            handler.Responder = _ =>
            {
                calls++;
                return Json(new { message = "boom" }, HttpStatusCode.InternalServerError);
            };

            if (v12)
            {
                await Assert.ThrowsAsync<ServiceOfflineException>(
                    () => client.UpdateBookmarksV12Async("id", "blob", 1));
            }
            else
            {
                await Assert.ThrowsAsync<ServiceOfflineException>(
                    () => client.UpdateBookmarksAsync("id", "blob", "t"));
            }

            Assert.Equal(1, calls);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, typeof(UnexpectedResponseException))]
    [InlineData(HttpStatusCode.PreconditionFailed, typeof(ServerPreconditionException))]
    public async Task ContractErrors_MapToTypedException(HttpStatusCode status, Type expected)
    {
        var (client, handler, _) = Create();
        handler.Responder = _ => Json(new { message = "contract" }, status);

        var ex = await Assert.ThrowsAsync(expected, () => client.GetLastUpdatedAsync("some-id"));
        Assert.Contains("contract", ex.Message);
    }

    [Fact]
    public async Task Throttled_HonorsRetryAfterDate()
    {
        var (client, handler, _) = Create();
        handler.Responder = _ =>
        {
            var r = Json(new { message = "slow" }, HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                DateTimeOffset.UtcNow.AddSeconds(45));
            return r;
        };

        // Create is non-idempotent: surfaces immediately without waiting out the delay.
        var ex = await Assert.ThrowsAsync<ThrottledException>(() => client.CreateSyncAsync());
        Assert.True(ex.RetryAfterSeconds is >= 30 and <= 60);
    }

    [Fact]
    public async Task GetInfoV12_SendsV12Header()
    {
        var (client, handler, _) = Create();
        string? version = null;
        handler.Responder = req =>
        {
            version = req.Headers.GetValues("Accept-Version").Single();
            return Json(new { location = "GB", maxSyncSize = 1, message = "", status = 1, version = "1.2.0" });
        };

        await client.GetInfoV12Async();

        Assert.Equal(XbsConstants.ApiVersion12, version);
    }

    [Fact]
    public async Task Responses_DeserializeMetaAndCapabilities()
    {
        var (client, handler, _) = Create();
        handler.Responder = req => req.RequestUri!.AbsolutePath.EndsWith("/info")
            ? Json(new { location = "GB", maxSyncSize = 1, message = "", status = 1, version = "1.2.0",
                capabilities = new { revision = true, conditionalGet = true, delete = true } })
            : Json(new { bookmarks = "b", version = "1.8.0", lastUpdated = "t",
                meta = new { revision = 4, hash = "h", size = 1 } });

        var info = await client.GetInfoAsync();
        var full = await client.GetBookmarksAsync("id");

        Assert.True(info.Capabilities?.Revision);
        Assert.Equal(4, full.Meta?.Revision);
        Assert.Equal("h", full.Meta?.Hash);
    }

    [Theory]
    [InlineData("21", 21)]
    [InlineData("0", 0)]
    [InlineData("-1", null)]
    [InlineData("1.5", null)]
    [InlineData("\"21\"", null)]
    [InlineData("null", null)]
    [InlineData(null, null)]
    public async Task SyncExpiryDays_MalformedNeverBreaksTheProbe(string? raw, int? expected)
    {
        var (client, handler, _) = Create();
        var expiry = raw is null ? string.Empty : $",\"syncExpiryDays\":{raw}";
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"maxSyncSize\":1,\"status\":1,\"version\":\"1.2.0\","
                + $"\"capabilities\":{{\"revision\":true,\"conditionalGet\":true,\"delete\":true{expiry}}}}}",
                System.Text.Encoding.UTF8, "application/json"),
        };

        var info = await client.GetInfoV12Async();

        Assert.True(info.Capabilities?.Revision); // a bad optional field never downgrades to v1.1
        Assert.Equal(expected, info.Capabilities?.SyncExpiryDays);
    }
}
