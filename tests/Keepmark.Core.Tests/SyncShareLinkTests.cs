using Keepmark.Core.Workflow;
using Xunit;

namespace Keepmark.Core.Tests;

public sealed class SyncShareLinkTests
{
    // Shared vector: Keepmark for browsers (tests/share-link.test.ts) pins the same string.
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string Id = "0123456789abcdef0123456789abcdef";
    private const string Vector = "keepmark://sync?v=1&s=https%3A%2F%2Fapi.example.test%2Fsync&id=0123456789abcdef0123456789abcdef&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    [Fact]
    public void BuildsTheSharedVector()
        => Assert.Equal(Vector, SyncShareLink.Build("https://api.example.test/sync", Id, Key));

    [Fact]
    public void ParsesTheSharedVectorAsKeyCandidate()
    {
        Assert.True(SyncShareLink.TryParse(Vector, out var payload));
        Assert.Equal("https://api.example.test/sync", payload!.ServiceUrl);
        Assert.Equal(Id, payload.Id);
        Assert.Equal(Key, payload.DerivedKey);
        Assert.Null(payload.Password);
        Assert.DoesNotContain(Key, payload.ToString());
    }

    [Fact]
    public void RoundTripsUrlSafeAlphabetAndHttp()
    {
        const string key = "//////////////////////////////////////////8=";
        var link = SyncShareLink.Build("http://192.168.1.5:8090/", "abc_DEF-1", key);
        Assert.DoesNotContain("/", link[SyncShareLink.Prefix.Length..].Split("&k=")[1]);
        Assert.True(SyncShareLink.TryParse(link, out var payload));
        Assert.Equal(key, payload!.DerivedKey);
        Assert.Equal(WorkflowRules.NormalizeServiceUrl("http://192.168.1.5:8090/"), payload.ServiceUrl);
    }

    [Fact]
    public void FindsTheLinkInsideAMessage()
    {
        Assert.True(SyncShareLink.TryParse($"Join my bookmarks:\n{Vector}\nthanks", out var payload));
        Assert.Equal(Id, payload!.Id);
        Assert.True(SyncShareLink.LooksLikeLink($"see {Vector.ToUpperInvariant()}"));
        Assert.False(SyncShareLink.LooksLikeLink("https://example.test/"));
    }

    [Theory]
    [InlineData("keepmark://sync?v=2&s=https%3A%2F%2Fa.test&id=abc&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("keepmark://sync?v=1&s=https%3A%2F%2Fa.test&id=abc")]
    [InlineData("keepmark://sync?v=1&s=https%3A%2F%2Fa.test&id=abc&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8&x=1")]
    [InlineData("keepmark://sync?v=1&v=1&s=https%3A%2F%2Fa.test&id=abc&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("keepmark://sync?v=1&s=ftp%3A%2F%2Fa.test&id=abc&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("keepmark://sync?v=1&s=https%3A%2F%2Fu%3Ap%40a.test&id=abc&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("keepmark://sync?v=1&s=https%3A%2F%2Fa.test%3Fq%3D1&id=abc&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("keepmark://sync?v=1&s=https%3A%2F%2Fa.test&id=a.b&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("keepmark://sync?v=1&s=https%3A%2F%2Fa.test&id=abc&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh")]
    [InlineData("keepmark://sync?v=1&s=https%3A%2F%2Fa.test&id=abc&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh9")]
    [InlineData("keepmark://sync?v=1&s=https%3A%2F%2Fa.test&id=abc&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8%3D")]
    [InlineData("keepmark://other?v=1&s=https%3A%2F%2Fa.test&id=abc&k=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsAnythingElse(string? text) => Assert.False(SyncShareLink.TryParse(text, out _));

    [Fact]
    public void BuildRefusesInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => SyncShareLink.Build("https://a.test", Id, "not-a-key"));
        Assert.Throws<ArgumentException>(() => SyncShareLink.Build("ftp://a.test", Id, Key));
        Assert.Throws<ArgumentException>(() => SyncShareLink.Build("https://a.test", "a b", Key));
    }
}
