using Keepmark.Core.Crypto;
using Keepmark.Core.I18n;

namespace Keepmark.Core.Workflow;

/// <summary>
/// Rules and formatting shared by every workflow page (docs/WORKFLOWS.md):
/// one password rule, one sync-ID short form, one size format.
/// </summary>
public static class WorkflowRules
{
    public static string PasswordTooShort => Loc.Get(Str.WfPasswordTooShort);
    public static string PasswordsDoNotMatch => Loc.Get(Str.WfPasswordsDoNotMatch);

    /// <summary>A password the user invents (new sync, move to a new sync,
    /// change password, backup export): at least
    /// <see cref="BackupGuard.MinPasswordLength"/> characters, typed twice.
    /// Returns the error to show, or null when valid.</summary>
    public static string? CheckNewPassword(string password, string confirm)
    {
        if (password.Length < BackupGuard.MinPasswordLength)
        {
            return PasswordTooShort;
        }

        return string.Equals(password, confirm, StringComparison.Ordinal) ? null : PasswordsDoNotMatch;
    }

    /// <summary>Throwing form of <see cref="CheckNewPassword"/> for step runners.</summary>
    public static void RequireNewPassword(string password, string confirm)
    {
        if (CheckNewPassword(password, confirm) is { } error)
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>Sync IDs are bearer tokens: screens show only "ab12…ef".</summary>
    public static string ShortId(string? id) =>
        id is null || id.Length <= 8 ? "…" : $"{id[..4]}…{id[^2..]}";

    /// <summary>Service URL as stored and shown: trimmed, scheme and host in
    /// lower case (Android keyboards capitalize the first letter: "Https://…").
    /// Path, port and anything not an absolute http(s) URL are kept as typed.</summary>
    public static string NormalizeServiceUrl(string? url)
    {
        var s = (url ?? string.Empty).Trim();
        var sep = s.IndexOf("://", StringComparison.Ordinal);
        if (sep < 0 || !Uri.TryCreate(s, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
        {
            return s;
        }

        var rest = s[(sep + 3)..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? string.Empty : rest[slash..];
        return $"{u.Scheme}://{authority.ToLowerInvariant()}{path}";
    }

    /// <summary>Scheme assumed when the user types only the host part in the
    /// custom-service editor. HTTPS only: cleartext would send the sync
    /// password and the bookmark blob unencrypted.</summary>
    public const string PreferredServiceScheme = "https";

    /// <summary>Schemes offered by the custom-service editor.</summary>
    public const string HttpScheme = "http";

    /// <summary>
    /// Splits an absolute http(s) URL into the pair the custom-service editor
    /// shows: the scheme for the picker and the rest ("host[:port][/path]",
    /// authority in lower case) for the text box. Anything that is not an
    /// absolute http(s) URL — including a bare "example.org/xbs" the user is
    /// typing — comes back whole, paired with <see cref="PreferredServiceScheme"/>.
    /// One parser for both directions, so composing it back is always the
    /// inverse of <see cref="NormalizeServiceUrl"/>.
    /// </summary>
    public static (string Scheme, string Host) SplitServiceUrl(string? url)
    {
        var s = (url ?? string.Empty).Trim();
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) ||
            (u.Scheme != HttpScheme && u.Scheme != PreferredServiceScheme))
        {
            return (PreferredServiceScheme, s);
        }

        var rest = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? string.Empty : rest[slash..];
        return (u.Scheme, $"{authority.ToLowerInvariant()}{path}");
    }

    /// <summary>Inverse of <see cref="SplitServiceUrl"/>: the URL to store and
    /// show. A scheme typed inside the host box is kept as typed (the user
    /// pasted a full URL); only a missing one is filled in, so the normal
    /// case is one short box. Empty host yields an empty string, never a
    /// dangling "https://" that no validator accepts.</summary>
    public static string ComposeServiceUrl(string? scheme, string? host)
    {
        var h = (host ?? string.Empty).Trim();
        if (h.Length == 0)
        {
            return string.Empty;
        }

        var s = (scheme ?? string.Empty).Trim();
        var sep = h.IndexOf("://", StringComparison.Ordinal);
        // A pasted scheme wins over the picker; "Https://x" resolves in C# even
        // though URI scheme matching is case-insensitive.
        if (sep > 0 && Uri.TryCreate(h, UriKind.Absolute, out var pasted) &&
            (pasted.Scheme == HttpScheme || pasted.Scheme == PreferredServiceScheme))
        {
            return h;
        }

        if (s != HttpScheme && s != PreferredServiceScheme)
        {
            s = PreferredServiceScheme;
        }

        return $"{s}://{h.TrimStart('/', ':')}";
    }

    /// <summary>Server's inactive-sync expiry (v1.2 <c>/info</c> capabilities):
    /// "—" when unknown (legacy or offline service).</summary>
    public static string DescribeSyncExpiry(int? days) => days switch
    {
        null => "—",
        0 => Loc.Get(Str.StatusExpiryNever),
        _ => Loc.Plural(Str.StatusExpiryDays, days.Value),
    };

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024L * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024):0.##} MB";
    }
}
