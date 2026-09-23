using Xunit;

namespace Keepmark.Core.Tests;

public sealed class VersionCompareTests
{
    [Theory]
    // The live bug: ordinal string compare said 1.1.13 < 1.1.9.
    [InlineData("1.1.13", "1.1.9", 1)]
    [InlineData("1.1.9", "1.1.13", -1)]
    [InlineData("1.1.9", "1.1.9", 0)]
    [InlineData("1.1.10", "1.1.9", 1)]
    [InlineData("1.8.0", "1.8.0", 0)]
    [InlineData("2.0", "1.9.9", 1)]
    [InlineData("1.1", "1.1.0", 0)]
    [InlineData("v1.1.13", "1.1.9", 1)]
    [InlineData("1.1.13-beta", "1.1.13", 0)]
    public void ComparesNumerically(string a, string b, int expected)
    {
        Assert.Equal(expected, XbsConstants.CompareVersions(a, b));
    }

    [Fact]
    public void CurrentApiIsAboveMinimum()
    {
        Assert.True(XbsConstants.CompareVersions(XbsConstants.ApiVersion, XbsConstants.MinApiVersion) > 0);
    }
}
