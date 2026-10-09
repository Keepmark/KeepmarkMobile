namespace Keepmark.Core.Security;

// Pure app-lock timing rules (no platform clock, no UI): the MAUI side feeds
// in the stored setting and a monotonic elapsed time.
//
// "Relock after" values are minutes: 0 = as soon as the app goes to the
// background, RestartOnly (-1) = only on a cold start. A cold start with app
// lock on always locks, so there is no separate "never" value (that is Off).

public static class LockPolicy
{
    public const int RestartOnly = -1;
    public const int DefaultMinutes = 5;

    /// <summary>Selectable values, strictest first (RestartOnly is the laxest).</summary>
    public static readonly int[] Presets = { 0, 1, 5, 15, 30, 60, 240, RestartOnly };

    /// <summary>Maps any stored value onto a preset. Values between presets
    /// round DOWN (stricter), so migrating an old free-form timeout never
    /// weakens the lock. Unknown negatives become 0.</summary>
    public static int Snap(int stored)
    {
        if (stored == RestartOnly)
        {
            return RestartOnly;
        }

        if (stored <= 0)
        {
            return 0;
        }

        var best = 0;
        foreach (var p in Presets)
        {
            if (p >= 0 && p <= stored)
            {
                best = p;
            }
        }

        return best;
    }

    /// <summary>Lock right when the app goes to the background.</summary>
    public static bool LockOnPause(int after) => Snap(after) == 0;

    /// <summary>Lock on return to the foreground after <paramref name="elapsedMs"/>
    /// in the background. RestartOnly never locks here.</summary>
    public static bool LockOnResume(int after, long elapsedMs)
    {
        var a = Snap(after);
        return a != RestartOnly && elapsedMs >= a * 60_000L;
    }

    /// <summary>True when <paramref name="to"/> keeps the app unlocked longer
    /// than <paramref name="from"/> (a security downgrade: needs the device PIN).</summary>
    public static bool IsLaxer(int from, int to) => Rank(Snap(to)) > Rank(Snap(from));

    private static long Rank(int v) => v == RestartOnly ? long.MaxValue : v;

    /// <summary>Short English label for a preset.</summary>
    public static string Label(int after) => Snap(after) switch
    {
        0 => "Immediately",
        1 => "After 1 minute",
        60 => "After 1 hour",
        240 => "After 4 hours",
        RestartOnly => "Only when the app restarts",
        var m => $"After {m} minutes",
    };
}
