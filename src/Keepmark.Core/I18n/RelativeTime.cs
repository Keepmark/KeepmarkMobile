using System.Globalization;

namespace Keepmark.Core.I18n;

/// <summary>
/// "Added" labels for synced dateAdded values: just now, N min, N hours (same
/// day), yesterday, N days (up to 6), N weeks (up to 29 days), then the date.
/// Calendar steps use the given time zone. Labels go stale as time passes: the UI re-renders them.
/// </summary>
public static class RelativeTime
{
    public static string Format(long unixMs, DateTimeOffset now, TimeZoneInfo zone)
    {
        var then = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        var ago = now - then;
        if (ago < TimeSpan.FromMinutes(1))
        {
            // Includes small future skew from another device's clock.
            return Loc.Get(Str.TimeNow);
        }

        if (ago < TimeSpan.FromHours(1))
        {
            return Loc.Plural(Str.TimeMinutes, (long)ago.TotalMinutes);
        }

        var localThen = TimeZoneInfo.ConvertTime(then, zone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var days = (localNow.Date - localThen.Date).Days;
        if (days <= 0)
        {
            return Loc.Plural(Str.TimeHours, (long)ago.TotalHours);
        }

        if (days == 1)
        {
            return Loc.Get(Str.TimeYesterday);
        }

        if (days < 7)
        {
            return Loc.Plural(Str.TimeDays, days);
        }

        if (days < 30)
        {
            return Loc.Plural(Str.TimeWeeks, days / 7);
        }

        var culture = CultureInfo.GetCultureInfo(Langs.Current);
        return localThen.Year == localNow.Year
            ? localThen.ToString(culture.DateTimeFormat.MonthDayPattern, culture)
            : localThen.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
    }

    /// <summary>Exact date + time for details ("g" pattern of the UI language), e.g. "14/10/2026 19:27".</summary>
    public static string Absolute(long unixMs, TimeZoneInfo zone)
    {
        var culture = CultureInfo.GetCultureInfo(Langs.Current);
        return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs), zone).ToString("g", culture);
    }

    /// <summary>Details line: exact date + time, then the relative label while
    /// it is not a plain date yet (under 30 days).</summary>
    public static string Detail(long unixMs, DateTimeOffset now, TimeZoneInfo zone)
    {
        var exact = Absolute(unixMs, zone);
        var then = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs), zone);
        return (TimeZoneInfo.ConvertTime(now, zone).Date - then.Date).Days < 30
            ? $"{exact} · {Format(unixMs, now, zone)}"
            : exact;
    }

    /// <summary>Details line for now in the device time zone; null when the node has no date.</summary>
    public static string? Detail(long? unixMs) =>
        unixMs is long ms ? Detail(ms, DateTimeOffset.UtcNow, TimeZoneInfo.Local) : null;

    /// <summary>Label for now in the device time zone; null when the node has no date.</summary>
    public static string? Format(long? unixMs) =>
        unixMs is long ms ? Format(ms, DateTimeOffset.UtcNow, TimeZoneInfo.Local) : null;
}
