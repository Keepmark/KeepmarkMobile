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

    [Theory]
    [InlineData("http://my.example")]
    [InlineData("https://user:secret@my.example")]
    [InlineData("https://my.example?secret=x")]
    [InlineData("invalid")]
    public void UnsafeUrlsAreNotRemembered(string url) => Assert.Empty(ServiceCatalog.Remember([], url));
}
