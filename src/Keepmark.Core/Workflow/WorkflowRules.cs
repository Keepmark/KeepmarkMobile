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
