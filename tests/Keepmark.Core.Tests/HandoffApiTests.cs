using System.Net;
using System.Text;
using Keepmark.Core.Api;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;

namespace Keepmark.Core.Tests;

public sealed class HandoffApiTests
{
    private const string HandoffId = "tGggaQeJDZf0F3QGzcz8iro8a28lqeDJlyvuhXEP6ys";
    private const string SyncId = "0123456789abcdef0123456789abcdef";

    private static (XSyncApiClient client, FakeHandler handler, InMemoryLogger log, List<string> bodies) Create(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var bodies = new List<string>();
        var handler = new FakeHandler();
        handler.Responder = req =>
        {
            bodies.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty);
            return responder(req);
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var log = new InMemoryLogger { MinLevel = XbsLogLevel.Verbose };
        return (new XSyncApiClient(http, new XbsLocalizer(), log), handler, log, bodies);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? attemptsLeft = null)
    {
        var res = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (attemptsLeft is not null) res.Headers.TryAddWithoutValidation("X-Handoff-Attempts-Left", attemptsLeft);
        return res;
    }

    [Fact]
    public async Task Create_SendsV12AndNeverLogsTheVoucherOrSyncId()
    {
        var (client, handler, log, bodies) = Create(_ => Json("""{"expiresAt":"2026-11-14T10:00:00.000Z","codeRequired":true}""", HttpStatusCode.Created));
        var res = await client.CreateHandoffAsync(SyncId, HandoffId, "cGF5bG9hZA", 600, new string('a', 64));
        Assert.Equal("2026-11-14T10:00:00.000Z", res.ExpiresAt);
        Assert.True(res.CodeRequired);
        Assert.Equal($"https://example.test/bookmarks/{SyncId}/handoffs", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("1.2.0", handler.LastRequest.Headers.GetValues("Accept-Version").Single());
        Assert.Contains("\"ttlSeconds\":600", bodies[0]);
        Assert.Contains("\"authHash\"", bodies[0]);
        var text = string.Join("\n", log.Entries.Select(e => e.Message));
        Assert.DoesNotContain(HandoffId, text);
        Assert.DoesNotContain(SyncId, text);
    }

    [Fact]
    public async Task Redeem_ReturnsThePayloadInOneAttempt()
    {
        var calls = 0;
        var (client, _, _, bodies) = Create(_ => { calls++; return Json("""{"payload":"cGF5bG9hZA"}"""); });
        Assert.Equal("cGF5bG9hZA", await client.RedeemHandoffAsync(HandoffId, null));
        Assert.Equal(1, calls);
        Assert.Equal("{}", bodies[0]);
    }

    [Theory]
    [InlineData(403, """{"code":"HandoffAuthFailedException"}""", "2", typeof(HandoffAuthFailedException))]
    [InlineData(403, """{"code":"HandoffAuthFailedException"}""", "0", typeof(HandoffNotFoundException))]
    [InlineData(404, """{"code":"HandoffNotFoundException"}""", null, typeof(HandoffNotFoundException))]
    [InlineData(404, """{"code":"NotImplementedException"}""", null, typeof(HandoffUnavailableException))]
    [InlineData(406, """{"code":"HandoffLimitExceededException"}""", null, typeof(HandoffLimitException))]
    [InlineData(412, "{}", null, typeof(HandoffUnavailableException))]
    [InlineData(401, "{}", null, typeof(SyncNotFoundException))]
    [InlineData(503, "{}", null, typeof(ServiceOfflineException))]
    public async Task Redeem_MapsHandoffErrors(int status, string body, string? left, Type expected)
    {
        var calls = 0;
        var (client, _, _, _) = Create(_ => { calls++; return Json(body, (HttpStatusCode)status, left); });
        var ex = await Assert.ThrowsAnyAsync<XbsException>(() => client.RedeemHandoffAsync(HandoffId, "auth"));
        Assert.IsType(expected, ex);
        Assert.Equal(1, calls);
        if (ex is HandoffAuthFailedException auth) Assert.Equal(2, auth.AttemptsLeft);
    }

    [Fact]
    public async Task Revoke_TrueWhenDeletedFalseWhenGone()
    {
        var (ok, _, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.True(await ok.RevokeHandoffAsync(SyncId, HandoffId));
        var (gone, _, _, _) = Create(_ => Json("""{"code":"HandoffNotFoundException"}""", HttpStatusCode.NotFound));
        Assert.False(await gone.RevokeHandoffAsync(SyncId, HandoffId));
    }

    [Fact]
    public void Capability_IsReadDefensively()
    {
        static ApiCapabilities Parse(string json) => System.Text.Json.JsonSerializer.Deserialize<ApiCapabilities>(json)!;
        var caps = Parse("""{"revision":true,"handoff":{"defaultTtlSeconds":600,"maxTtlSeconds":3600,"maxActivePerSync":5,"maxAttempts":5,"maxPayloadBytes":4096}}""");
        Assert.Equal(3600, caps.Handoff!.MaxTtlSeconds);
        Assert.Null(Parse("""{"revision":true}""").Handoff);
        Assert.Null(Parse("""{"revision":true,"handoff":{"maxTtlSeconds":"x"}}""").Handoff);
        Assert.Null(Parse("""{"revision":true,"handoff":true}""").Handoff);
    }
}
