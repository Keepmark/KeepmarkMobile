using System.Text.RegularExpressions;

namespace Keepmark.Core.Workflow;

/// <summary>
/// Shared sync link, the text form of the connect QR (format 2):
/// <c>keepmark://sync?v=1&amp;s=&lt;service URL, percent-encoded&gt;&amp;id=&lt;sync ID&gt;&amp;k=&lt;derived key, base64url&gt;</c>.
/// It carries the derived key, never the password. Unlike the QR it is a
/// lasting credential (it stays in chats and clipboards): anyone holding it
/// can read and change the bookmarks until the sync password is changed.
/// The same format is produced and read by Keepmark for browsers.
/// </summary>
public static partial class SyncShareLink
{
    public const string Scheme = "keepmark";
    public const string Host = "sync";
    public const string Prefix = "keepmark://sync?";
    public const int FormatVersion = 1;
    /// <summary>Upper bound for a link (and for the text scanned for one).</summary>
    public const int MaxLength = 2048;

    /// <summary>Builds the link from a saved sync's derived key (canonical
    /// base64 of 32 bytes). Throws on anything the parser would refuse.</summary>
    public static string Build(string serviceUrl, string syncId, string derivedKey)
    {
        if (!ConnectQrPayload.IsServiceUrl(serviceUrl) || !ConnectQrPayload.IsSyncId(syncId) || !ConnectQrPayload.IsDerivedKey(derivedKey))
            throw new ArgumentException("Sync link input is not valid");
        var key = derivedKey.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var link = $"{Prefix}v={FormatVersion}&s={Uri.EscapeDataString(WorkflowRules.NormalizeServiceUrl(serviceUrl))}&id={syncId}&k={key}";
        if (link.Length > MaxLength) throw new ArgumentException("Sync link too long");
        return link;
    }

    /// <summary>True when the text holds something that looks like a sync
    /// link (valid or not): such text must never be saved as a bookmark.</summary>
    public static bool LooksLikeLink(string? text)
        => !string.IsNullOrEmpty(text) && text.Contains(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds the first sync link in <paramref name="text"/> (a pasted
    /// message may carry words around it) and validates it strictly: exactly
    /// the four parameters once each, format 1, safe ID, canonical 32-byte key,
    /// absolute https/http service URL. Nothing touches the network.</summary>
    public static bool TryParse(string? text, out ConnectQrPayload? payload)
    {
        payload = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength * 4) return false;
        var match = LinkRegex().Match(text);
        if (!match.Success || match.Length > MaxLength) return false;
        var query = match.Value[Prefix.Length..];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) return false;
            var name = part[..eq];
            if (name is not ("v" or "s" or "id" or "k") || !values.TryAdd(name, part[(eq + 1)..])) return false;
        }
        if (values.Count != 4 || values["v"] != FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
        string url;
        try { url = Uri.UnescapeDataString(values["s"]); }
        catch (UriFormatException) { return false; }
        var id = values["id"];
        var raw = values["k"];
        if (raw.Length != 43 || !Base64UrlRegex().IsMatch(raw)) return false;
        var key = raw.Replace('-', '+').Replace('_', '/') + "=";
        if (!ConnectQrPayload.IsSyncId(id) || !ConnectQrPayload.IsDerivedKey(key) || !ConnectQrPayload.IsServiceUrl(url)) return false;
        payload = ConnectQrPayload.FromShareLink(url, id, key);
        return true;
    }

    [GeneratedRegex(@"keepmark://sync\?[A-Za-z0-9._~%&=+-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{43}\z")]
    private static partial Regex Base64UrlRegex();
}
