using System.Text.Json;
using Keepmark.Core.Workflow;
using Xunit;

namespace Keepmark.Core.Tests;

public sealed class ConnectQrTests
{
    private static string Fixture(string password = "  Example only: è+日本語 \"\\😀  ") => JsonSerializer.Serialize(new
    {
        app = "Keepmark", format = 1, serviceType = "xbrowsersync",
        serviceUrl = "https://sync.example.test:8443/prefix", id = "0123456789abcdef0123456789abcdef",
        version = "1.8.0", password
    });

    [Fact]
    public void PreservesLiteralSecretAndOnlyAllowedFields()
    {
        const string secret = "  Example only: è+日本語 \"\\😀  ";
        Assert.True(ConnectQrPayload.TryParse(Fixture(secret), out var payload));
        Assert.Equal(secret, payload!.Password);
        Assert.Equal("https://sync.example.test:8443/prefix", payload.ServiceUrl);
        Assert.DoesNotContain(secret, payload.ToString());
        Assert.True(ConnectQrPayload.TryParse(Fixture("x")[..^1] + ",\"ignored\":true}", out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("{\"app\":\"Keepmark\",\"app\":\"Keepmark\"}")]
    public void RejectsMalformedInput(string text) => Assert.False(ConnectQrPayload.TryParse(text, out _));

    [Theory]
    [InlineData("serviceUrl", "http://sync.example.test")]
    [InlineData("serviceUrl", "https://user:pass@sync.example.test")]
    [InlineData("serviceUrl", "https://@sync.example.test")]
    [InlineData("serviceUrl", "https://sync.example.test?query")]
    [InlineData("serviceUrl", "https://sync.example.test#fragment")]
    [InlineData("serviceUrl", "https://sync.example.test/\\path")]
    [InlineData("serviceUrl", "https://sync.example.test/%0a")]
    [InlineData("id", "a/b")]
    [InlineData("id", "a%2fb")]
    [InlineData("id", "a b")]
    [InlineData("version", "1.9.0")]
    [InlineData("version", "1.8")]
    [InlineData("version", "v1.8.0")]
    [InlineData("app", "keepmark")]
    [InlineData("serviceType", "other")]
    [InlineData("password", "")]
    public void RejectsInvalidField(string field, string value)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Fixture())!;
        fields[field] = JsonSerializer.SerializeToElement(value);
        Assert.False(ConnectQrPayload.TryParse(JsonSerializer.Serialize(fields), out _));
    }

    [Fact]
    public void RejectsTypesDuplicatesDepthAndUtf8Limit()
    {
        Assert.False(ConnectQrPayload.TryParse(Fixture().Replace("\"format\":1", "\"format\":1.0"), out _));
        Assert.False(ConnectQrPayload.TryParse(Fixture().Replace("\"format\":1", "\"format\":\"1\""), out _));
        Assert.False(ConnectQrPayload.TryParse(Fixture()[..^1] + ",\"password\":\"other\"}", out _));
        Assert.False(ConnectQrPayload.TryParse(Fixture()[..^1] + ",\"extra\":{\"a\":{\"b\":{\"c\":{}}}}}", out _));
        Assert.False(ConnectQrPayload.TryParse(Fixture(new string('界', 1800)), out _));
        var local = Fixture().Replace("https://sync.example.test:8443/prefix", "http://localhost:8090/prefix");
        Assert.False(ConnectQrPayload.TryParse(local, out _));
        Assert.True(ConnectQrPayload.TryParse(local, out _, allowDevelopmentLocalhost: true));
    }
}

public sealed class ConnectQrCandidateStateTests
{
    private sealed class Clock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
    private static ConnectQrPayload Payload()
    {
        Assert.True(ConnectQrPayload.TryParse("{\"app\":\"Keepmark\",\"format\":1,\"serviceType\":\"xbrowsersync\",\"serviceUrl\":\"https://sync.example.test:8443/prefix\",\"id\":\"0123456789abcdef0123456789abcdef\",\"version\":\"1.8.0\",\"password\":\"Example only: è+日本語\"}", out var payload));
        return payload!;
    }
    [Fact]
    public void PendingHasNoSideEffectsAndCanOnlyBeConsumedOnce()
    {
        var state = new ConnectQrCandidateState();
        var candidate = Payload();
        Assert.True(state.Offer(candidate, state.Generation));
        Assert.Same(candidate, state.Payload);
        Assert.False(state.Offer(Payload(), state.Generation));
        Assert.Same(candidate, state.Take());
        Assert.Null(state.Payload);
        Assert.Null(state.Take());
    }
    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    [InlineData(120, false)]
    public void DeadlineCheckedAtConsumptionEvenWhenTimerDidNotRun(long seconds, bool accepted)
    {
        var clock = new Clock();
        var state = new ConnectQrCandidateState(clock);
        state.Offer(Payload(), state.Generation);
        clock.Seconds = seconds;
        Assert.Equal(accepted, state.Take() is not null);
        Assert.Null(state.Payload);
    }
    [Fact]
    public void CancellationInvalidatesLateOrDuplicateScanResults()
    {
        var state = new ConnectQrCandidateState();
        var generation = state.Generation;
        state.Clear(); // Back, background, lock or dispose all use this operation.
        Assert.False(state.Offer(Payload(), generation));
        Assert.Null(state.Payload);
        Assert.True(state.Offer(Payload(), state.Generation));
        state.Clear();
        Assert.Null(state.Take());
    }
    [Fact]
    public async Task DelayedFakeScannerCannotRestoreCancelledCandidate()
    {
        var state = new ConnectQrCandidateState();
        var scanner = new TaskCompletionSource<ConnectQrPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = state.Generation;
        async Task<bool> Read() => state.Offer(await scanner.Task, generation);
        var read = Read();
        state.Clear();
        scanner.SetResult(Payload());
        Assert.False(await read);
        Assert.Null(state.Payload);
    }

    [Fact]
    public async Task ProbeReceivesExactCredentialsAfterCandidateWasRemovedAndFailureCannotRetry()
    {
        var state = new ConnectQrCandidateState();
        var payload = Payload();
        var calls = 0;
        state.Offer(payload, state.Generation);
        Assert.Equal(0, calls);
        var candidate = state.Take();
        async Task Probe(string url, string id, string password)
        {
            calls++;
            Assert.Null(state.Payload);
            Assert.Equal(payload.ServiceUrl, url);
            Assert.Equal(payload.Id, id);
            Assert.Equal(payload.Password, password);
            await Task.Yield();
            throw new InvalidOperationException("Synthetic offline probe");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Probe(candidate!.ServiceUrl, candidate.Id, candidate.Password));
        Assert.Equal(1, calls);
        Assert.Null(state.Take());
    }

}

public sealed class ConnectQrReviewPhaseTests
{
    private static ConnectQrCandidateState Scanned()
    {
        Assert.True(ConnectQrPayload.TryParse("{\"app\":\"Keepmark\",\"format\":1,\"serviceType\":\"xbrowsersync\",\"serviceUrl\":\"https://sync.example.test/path\",\"id\":\"synthetic-id\",\"version\":\"1.8.0\",\"password\":\"synthetic-only\"}", out var payload));
        var state = new ConnectQrCandidateState();
        Assert.True(state.Offer(payload!, state.Generation));
        return state;
    }

    [Fact]
    public async Task DelayedVerificationKeepsReviewVisibleWithoutRetainingCandidateSecret()
    {
        var state = Scanned();
        Assert.True(state.ShowReview);
        Assert.False(state.IsVerifying);
        var candidate = state.BeginVerification();
        Assert.NotNull(candidate);
        Assert.Null(state.Payload);
        Assert.True(state.ShowReview);
        Assert.True(state.IsVerifying);
        Assert.False(state.Offer(candidate!, state.Generation));
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Probe() { await finish.Task; state.Clear(); }
        var probe = Probe();
        Assert.True(state.ShowReview);
        finish.SetResult(); await probe;
        Assert.False(state.ShowReview);
        Assert.False(state.IsVerifying);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelBackLockOrFailureClearsBothPendingAndVerifyingReview(bool verifying)
    {
        var state = Scanned();
        if (verifying) state.BeginVerification();
        var generation = state.Generation;
        state.Clear();
        Assert.False(state.ShowReview);
        Assert.False(state.IsVerifying);
        Assert.Null(state.Payload);
        Assert.NotEqual(generation, state.Generation);
        Assert.Null(state.BeginVerification());
        Assert.False(state.ShowReview);
    }
}
