using Keepmark.Core.Crypto;

namespace Keepmark.Core.Workflow;

/// <summary>
/// Rules and formatting shared by every workflow page (docs/WORKFLOWS.md):
/// one password rule, one sync-ID short form, one size format.
/// </summary>
public static class WorkflowRules
{
    public const string PasswordTooShort = "Use a password of at least 8 characters.";
    public const string PasswordsDoNotMatch = "Passwords do not match.";

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
