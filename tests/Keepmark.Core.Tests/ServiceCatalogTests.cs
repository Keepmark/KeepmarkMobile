using Keepmark.Core.Workflow;
using Xunit;

namespace Keepmark.Core.Tests;

public class ServiceCatalogTests
{
    [Fact]
    public void DefaultFirstAndNoDuplicates()
    {
        Assert.Equal("https://api.xbrowsersync.org", ServiceCatalog.Public[0].Url);
        Assert.Equal(8, ServiceCatalog.Public.Count);
        Assert.Equal(8, ServiceCatalog.Public.Select(x => x.Url).Distinct().Count());
    }

    [Fact]
    public void SuccessfulCustomIsNormalizedAndDeduplicated()
    {
        var saved = ServiceCatalog.Remember([], "https://my.example/");
        saved = ServiceCatalog.Remember(saved, "https://my.example");
        Assert.Equal(new[] { "https://my.example" }, saved);
        Assert.Equal(saved, ServiceCatalog.Remember(saved, ServiceCatalog.Public[0].Url));
    }

    [Fact]
    public void LateResultCannotEnableAnotherSelectedService()
    {
        var state = new ServiceCheckState();
        var first = state.Invalidate();
        var second = state.Invalidate();
        Assert.False(state.Complete(first, Keepmark.Core.Models.ServiceStatus.Online));
        Assert.False(state.Ready);
        Assert.True(state.Complete(second, Keepmark.Core.Models.ServiceStatus.NoNewSyncs));
        Assert.True(state.Ready);
        Assert.False(state.CanCreate);
        state.Invalidate();
        Assert.False(state.Ready);
        Assert.False(state.CanCreate);
    }

    [Fact]
    public void CanonicalServiceIdentityIgnoresHostCaseAndDefaultPort()
    {
        Assert.Equal("https://example.com/api", ServiceCatalog.Normalize("HTTPS://EXAMPLE.COM:443/api/"));
        Assert.True(ServiceCatalog.IsPublic("https://API.XBROWSERSYNC.ORG:443/"));
        Assert.Single(ServiceCatalog.Remember(["https://EXAMPLE.com:443/"], "https://example.com"));
        Assert.NotEqual(ServiceCatalog.Normalize("https://example.com:8443/api"), ServiceCatalog.Normalize("https://example.com/api"));
        Assert.NotEqual(ServiceCatalog.Normalize("https://example.com/API"), ServiceCatalog.Normalize("https://example.com/api"));
    }

    [Theory]
    [InlineData("http://my.example")]
    [InlineData("https://user:secret@my.example")]
    [InlineData("https://my.example?secret=x")]
    [InlineData("invalid")]
    public void UnsafeUrlsAreNotRemembered(string url) => Assert.Empty(ServiceCatalog.Remember([], url));
}
