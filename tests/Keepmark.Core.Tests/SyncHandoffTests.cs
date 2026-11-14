using Keepmark.Core.Workflow;
using Xunit;

namespace Keepmark.Core.Tests;

/// <summary>Reference vector: KeepmarkApi HandoffTests.ClientVector_* and the
/// browser extension's tests/handoff.test.ts. Never change one side alone.</summary>
public class SyncHandoffTests
{
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Nonce = Enumerable.Range(0, 12).Select(i => (byte)(0xa0 + i)).ToArray();
    private const string Token = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    private const string HandoffId = "tGggaQeJDZf0F3QGzcz8iro8a28lqeDJlyvuhXEP6ys";
    private const string EncKey = "1030d5c122b6aa162a6c7661491fe21f7d1506e1f5906ef4267f02d864f505fe";
    private const string EncKeyNoCode = "4b96080ce8f6b3acf759b2d7521fa391c123675a31bef4be967a503df0c9c692";
    private const string Auth = "5MgSvJvzkiItQgb54bdjmJXprBtSWLThiXDGISEiBRc";
    private const string AuthHash = "f00e72ccdcc85e56bacee9e012694482fd1b5abaf8d5b0f6d93e34b6198eacdc";
    private const string Service = "https://sync.example.com/api";
    private const string Id = "0123456789abcdef0123456789abcdef";
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string Payload =
        "oKGio6SlpqeoqaqrpzCWtg5_WbR5gSWr_KBfQOO5mQ6CE_s-xiw9HZFjd7L4k_payhKpkeDsu8Tx7EU-CtiiLM-Pzlc0uB4jNhlXHXoxrHof0iF4e-aDwKJ5OIl1N4OgA7cLZvgRboF3IYHKdvMhUKdXf06pB9w1HlOC5_7ju0ORthRTbWS2MYYpNBJ4U9wo7Kz639Ku2eBWKk7n4-vI_ptRCBZDOsGtfENv8KgI";
    private const string Link = "keepmark://sync?v=2&s=https%3A%2F%2Fsync.example.com%2Fapi&t=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8&c=1";

    [Fact]
    public void DerivesTheServerReferenceVector()
    {
        var (hid, enc, auth) = SyncHandoff.Keys(Secret, "123456");
        Assert.Equal(HandoffId, hid);
        Assert.Equal(EncKey, Convert.ToHexStringLower(enc));
        Assert.Equal(Auth, auth);
        Assert.Equal(AuthHash, SyncHandoff.AuthHash(Auth));
        var (hid2, enc2, auth2) = SyncHandoff.Keys(Secret);
        Assert.Equal(HandoffId, hid2);
        Assert.Equal(EncKeyNoCode, Convert.ToHexStringLower(enc2));
        Assert.Null(auth2);
    }

    [Fact]
    public void BuildsThePayloadAndLinkVectorByteForByte()
    {
        var made = SyncHandoff.Create(new HandoffContent(Service, Id, Key, "Laptop"), "123456", Secret, Nonce);
        Assert.Equal(Link, made.Link);
        Assert.Equal(HandoffId, made.HandoffId);
        Assert.Equal(Payload, made.Payload);
        Assert.Equal(AuthHash, made.AuthHash);
    }

    [Fact]
    public void ParsesRebuildsTheRedeemRequestAndOpensThePayload()
    {
        Assert.True(SyncHandoff.TryParse($"Open this: {Link} (code by phone)", out var link));
        Assert.Equal(Service, link!.ServiceUrl);
        Assert.Equal(Token, link.Token);
        Assert.True(link.CodeRequired);
        Assert.Equal((HandoffId, (string?)Auth), SyncHandoff.RedeemRequest(link, "123456"));
        var content = SyncHandoff.Open(link, Payload, "123456");
        Assert.NotNull(content);
        Assert.Equal((Service, Id, Key, "Laptop"), (content!.ServiceUrl, content.Id, content.Key, content.Label));
        Assert.Null(SyncHandoff.Open(link, Payload, "654321"));
        Assert.False(SyncShareLink.TryParse(Link, out _));
        Assert.DoesNotContain(Key, content.ToString());
    }

    [Fact]
    public void RoundTripsWithoutACodeAndSendsNoAuth()
    {
        var made = SyncHandoff.Create(new HandoffContent(Service, Id, Key, null));
        Assert.Null(made.AuthHash);
        Assert.DoesNotContain("&c=1", made.Link);
        Assert.True(SyncHandoff.TryParse(made.Link, out var link));
        Assert.False(link!.CodeRequired);
        Assert.Equal((made.HandoffId, (string?)null), SyncHandoff.RedeemRequest(link));
        var content = SyncHandoff.Open(link, made.Payload)!;
        Assert.Equal((Id, Key, (string?)null), (content.Id, content.Key, content.Label));
    }

    [Fact]
    public void RefusesAPayloadReplayedUnderAnotherService()
    {
        var made = SyncHandoff.Create(new HandoffContent(Service, Id, Key, null));
        Assert.True(SyncHandoff.TryParse(made.Link, out var link));
        Assert.Null(SyncHandoff.Open(new HandoffSecret("https://evil.example.com/api", link!.Token, false), made.Payload));
    }

    [Theory]
    [InlineData("keepmark://sync?v=2&s=https%3A%2F%2Fa.test&t=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh")]
    [InlineData("keepmark://sync?v=2&s=https%3A%2F%2Fa.test&t=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh9")]
    [InlineData("keepmark://sync?v=2&s=https%3A%2F%2Fa.test&t=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8&c=2")]
    [InlineData("keepmark://sync?v=2&s=https%3A%2F%2Fa.test&t=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8&id=x")]
    [InlineData("keepmark://sync?v=2&s=ftp%3A%2F%2Fa.test&t=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("keepmark://sync?v=2&t=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("keepmark://sync?v=3&s=https%3A%2F%2Fa.test&t=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("keepmark://sync?v=2&v=2&s=https%3A%2F%2Fa.test&t=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    public void RejectsMalformedLinks(string text) => Assert.False(SyncHandoff.TryParse(text, out _));

    [Fact]
    public void GeneratesUniformSixDigitCodesAndSkipsTheBiasedTail()
    {
        Assert.Matches("^[0-9]{6}$", SyncHandoff.GenerateCode());
        var values = new Queue<uint>([4_294_967_295, 4_294_000_000, 7]);
        Assert.Equal("000007", SyncHandoff.GenerateCode(() => values.Dequeue()));
    }

    [Fact]
    public void DecodesBase64UrlStrictly()
    {
        Assert.Equal(Secret, SyncHandoff.FromBase64Url(Token));
        Assert.Equal(Token, SyncHandoff.ToBase64Url(Secret));
        Assert.Null(SyncHandoff.FromBase64Url(Token + "="));
        Assert.Null(SyncHandoff.FromBase64Url("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh9"));
    }

    [Fact]
    public void CleansLabels()
    {
        Assert.Equal("Laptop", SyncHandoff.CleanLabel("  Lap\u0007top "));
        Assert.Null(SyncHandoff.CleanLabel(" \t "));
        Assert.Equal(40, SyncHandoff.CleanLabel(new string('x', 60))!.Length);
    }
}
