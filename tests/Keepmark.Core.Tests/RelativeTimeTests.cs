using System.Globalization;
using Keepmark.Core.I18n;

namespace Keepmark.Core.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 19, 30, 0, TimeSpan.Zero);

    private static string Ago(TimeSpan span) =>
        RelativeTime.Format((Now - span).ToUnixTimeMilliseconds(), Now, TimeZoneInfo.Utc);

    [Fact]
    public void English_Steps()
    {
        Assert.Equal("just now", Ago(TimeSpan.FromSeconds(30)));
        Assert.Equal("just now", Ago(TimeSpan.FromMinutes(-3)));
        Assert.Equal("1 min ago", Ago(TimeSpan.FromSeconds(61)));
        Assert.Equal("59 min ago", Ago(TimeSpan.FromMinutes(59.9)));
        Assert.Equal("1 hour ago", Ago(TimeSpan.FromMinutes(61)));
        Assert.Equal("19 hours ago", Ago(TimeSpan.FromHours(19.5)));
        // Calendar day, not 24 h: 20:00 yesterday is "yesterday" at 19:30.
        Assert.Equal("yesterday", Ago(TimeSpan.FromHours(23.5)));
        Assert.Equal("2 days ago", Ago(TimeSpan.FromHours(44)));
        Assert.Equal("6 days ago", Ago(TimeSpan.FromDays(6)));
        Assert.Equal("1 week ago", Ago(TimeSpan.FromDays(7)));
        Assert.Equal("4 weeks ago", Ago(TimeSpan.FromDays(29)));
        Assert.Equal("September 1", Ago(Now - new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("12/24/2025", Ago(Now - new DateTimeOffset(2025, 12, 24, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void UsesTheGivenTimeZoneForCalendarDays()
    {
        // 23:30 UTC on the 13th is already the 14th in Rome (UTC+2): same day there.
        var then = new DateTimeOffset(2026, 10, 13, 23, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var rome = TimeZoneInfo.CreateCustomTimeZone("t+2", TimeSpan.FromHours(2), "t+2", "t+2");
        Assert.Equal("yesterday", RelativeTime.Format(then, Now, TimeZoneInfo.Utc));
        Assert.Equal("20 hours ago", RelativeTime.Format(then, Now, rome));
    }

    [Fact]
    public void Italian()
    {
        var saved = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("it-IT");
        try
        {
            Assert.Equal("adesso", Ago(TimeSpan.FromSeconds(5)));
            Assert.Equal("1 ora fa", Ago(TimeSpan.FromMinutes(90)));
            Assert.Equal("ieri", Ago(TimeSpan.FromHours(23.5)));
            Assert.Equal("3 giorni fa", Ago(TimeSpan.FromDays(3)));
            Assert.Equal("1 settembre", Ago(Now - new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)));
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }
    }
}
