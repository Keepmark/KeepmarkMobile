using Keepmark.Core.Security;
using Xunit;

namespace Keepmark.Core.Tests;

public sealed class LockPolicyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(10, 5)]   // old default migrates stricter, never laxer
    [InlineData(45, 30)]
    [InlineData(60, 60)]
    [InlineData(239, 60)]
    [InlineData(999, 240)]
    [InlineData(-1, -1)]
    [InlineData(-7, 0)]
    public void SnapRoundsDownToPreset(int stored, int expected)
    {
        Assert.Equal(expected, LockPolicy.Snap(stored));
    }

    [Fact]
    public void OnlyImmediateLocksOnPause()
    {
        Assert.True(LockPolicy.LockOnPause(0));
        Assert.False(LockPolicy.LockOnPause(1));
        Assert.False(LockPolicy.LockOnPause(LockPolicy.RestartOnly));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 59_999, false)]
    [InlineData(1, 60_000, true)]
    [InlineData(240, 4 * 3_600_000L - 1, false)]
    [InlineData(240, 4 * 3_600_000L, true)]
    [InlineData(-1, long.MaxValue, false)]
    public void ResumeLocksAtBoundary(int after, long elapsedMs, bool expected)
    {
        Assert.Equal(expected, LockPolicy.LockOnResume(after, elapsedMs));
    }

    [Theory]
    [InlineData(1, 240, true)]
    [InlineData(240, 1, false)]
    [InlineData(240, -1, true)]
    [InlineData(-1, 0, false)]
    [InlineData(5, 5, false)]
    [InlineData(10, 5, false)] // same preset after snap
    public void LaxerMeansLongerUnlocked(int from, int to, bool expected)
    {
        Assert.Equal(expected, LockPolicy.IsLaxer(from, to));
    }

    [Fact]
    public void EveryPresetHasALabel()
    {
        Assert.All(LockPolicy.Presets, p => Assert.False(string.IsNullOrWhiteSpace(LockPolicy.Label(p))));
        Assert.Equal("After 15 minutes", LockPolicy.Label(15));
    }
}
