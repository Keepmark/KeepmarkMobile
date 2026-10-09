namespace Keepmark.Core.Workflow;

/// <summary>
/// User-facing workflow texts: confirmations, implication lines and results.
/// One place to read and change them; wording follows docs/UI-TERMS.md and
/// docs/WORKFLOWS.md, kept identical to the browser extension
/// (KeepmarkApp src/popup/texts.ts) except "device" for "browser".
/// </summary>
public static class WorkflowTexts
{
    // Change password.
    public const string ChangePasswordImplication =
        "Your other devices stop syncing until you unlink them and link again with the new password. " +
        "Edits not yet synced are uploaded now. Older backup files keep their own password.";

    // Connect → Unlink.
    public const string UnlinkImplication =
        "Syncing stops; the bookmarks on this device stay. To link again you need the sync ID and its password, " +
        "so copy the sync ID first if you have not saved it. Edits not yet synced stay on this device only.";

    public const string UnlinkDone =
        "Device unlinked. Its bookmarks are untouched; link again with the sync ID and password to resume syncing.";

    // Backup → Export.
    public const string BackupPasswordHint =
        "It is not your sync password and cannot be recovered: without it the file cannot be opened.";

    public static string ExportDone(int count) =>
        $"Backup of {count} item(s) exported. Keep its password: it is not your sync password and cannot be recovered.";

    // Backup → Restore from a backup file.
    public static string ImportChecked(string fileName, int count) =>
        $"{fileName}: {count} item(s), password correct. Nothing has changed yet.";

    /// <summary>Unlinked, the mobile app restores this device only; linked, the
    /// restored tree is uploaded like any local edit.</summary>
    public static string ImportImplication(bool linked) => linked
        ? "This device's bookmarks and the server copy are replaced; your other devices get the backup at their next sync. " +
          "The current bookmarks are saved to Safety backups first."
        : "This device's bookmarks are replaced. The current bookmarks are saved to Safety backups first.";

    public static string ImportDone(int count, bool linked) => linked
        ? $"Restored {count} item(s); they are uploaded now and your other devices get them at their next sync. " +
          "The previous bookmarks are in Safety backups below."
        : $"Restored {count} item(s) on this device. The previous bookmarks are in Safety backups below.";

    // Backup → Safety backups.
    public static string SafetyRestoreImplication(string side, int items) => side == "local"
        ? $"This device's bookmarks are replaced with this copy ({items} items) and, while linked, uploaded; " +
          "your other devices get it at their next sync. The current bookmarks are saved as a safety backup first."
        : $"The server copy is replaced with this copy ({items} items); your other devices get it at their next sync. " +
          "The current server copy is saved as a safety backup first.";

    public static string SafetyRestoreDone(int count) =>
        $"Restored {count} item(s). The state before the restore is saved as a new safety backup.";

    public const string SafetyDeleteImplication = "This safety backup is gone for good.";
}
