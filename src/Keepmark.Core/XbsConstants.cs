// Interop constants. Verified against api/ (v1.1.13) and app/ sources.
// Do not change without checking server version routing (Accept-Version)
// and the sync-version compatibility rule (remote version must be <= SyncClientVersion).
namespace Keepmark.Core;

public static class XbsConstants
{
    public const string DefaultServiceUrl = "https://api.xbrowsersync.org";

    /// <summary>Minimum API version this client supports (mirrors app Globals.MinApiVersion).</summary>
    public const string MinApiVersion = "1.1.9";

    /// <summary>API version sent in the Accept-Version header. Must satisfy the server's ^1.1.3 route.</summary>
    public const string ApiVersion = "1.1.13";

    /// <summary>Extended contract (revision metadata, conditional GET). Sent only
    /// after the server advertises capabilities — legacy servers answer 412 to it.</summary>
    public const string ApiVersion12 = "1.2.0";

    /// <summary>
    /// Sync format version advertised when creating/upgrading a sync.
    /// Kept >= latest known client (1.8.0) so existing syncs are never rejected
    /// by the "remote version must be <= app version" rule. Independent from the APK version.
    /// </summary>
    public const string SyncClientVersion = "1.8.0";

    /// <summary>PBKDF2 iteration count used by the original JS client (crypto.service.ts).</summary>
    public const int KeyDerivationIterations = 250_000;

    /// <summary>AES-GCM nonce length in bytes (prepended to the ciphertext).</summary>
    public const int GcmNonceLength = 16;

    /// <summary>Maximum bookmark description length enforced by the original client.</summary>
    public const int DescriptionMaxLength = 300;

    /// <summary>Separator bookmark marker (bookmarks with this URL render as separators).</summary>
    public const string SeparatorUrl = "xbs:separator";

    /// <summary>Unused legacy delimiter; Keepmark connection QR uses explicit JSON v1.</summary>
    public const string QrDelimiter = "|$$|";

    /// <summary>Default auto-sync poll interval, mirrors the 15-minute background alarm.</summary>
    public static readonly TimeSpan SyncPollInterval = TimeSpan.FromMinutes(15);

    /// <summary>HTTP timeout for /info probes (mirrors checkServiceStatus 3000ms).</summary>
    public static readonly TimeSpan ServiceProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Default HTTP timeout for bookmark up/downloads (mirrors the 12s interceptor default).</summary>
    public static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Numeric dotted-version comparison ("1.1.13" &gt; "1.1.9"; never ordinal).
    /// Tolerates a leading 'v' and non-digit suffixes per part.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) => v.Trim().TrimStart('v', 'V').Split('.')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 0)
            .ToArray();
        var pa = Parts(a);
        var pb = Parts(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int d = (i < pa.Length ? pa[i] : 0) - (i < pb.Length ? pb[i] : 0);
            if (d != 0)
            {
                return Math.Sign(d);
            }
        }

        return 0;
    }
}
