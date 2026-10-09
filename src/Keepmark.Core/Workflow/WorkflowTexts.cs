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

    // Connect → new sync.
    public const string CreatedNext =
        "Copy the sync ID above and store it with the password: both are needed to link your other devices.";

    public const string CreateEmptyImplication =
        "This device starts empty and the empty sync is what your other devices get. " +
        "The current bookmarks are saved to Backup → Safety backups first.";

    // Direction choice (Connect, Move, Conflict): picker order and confirms.
    /// <summary>Keep the server copy / Merge both / Keep this device's copy.</summary>
    public static readonly string[] DirectionOrder = ["download", "merge", "upload"];

    public sealed record Confirm(string Message, string Implication, string ConfirmLabel);

    public static Confirm DirectionConfirm(string direction) => direction switch
    {
        "upload" => new("Replace the server copy with this device's bookmarks?",
            "Changes from your other devices that are only on the server are lost. A copy is saved to Backup → Safety backups first.",
            "Yes, upload"),
        "merge" => new("Merge both copies into one and sync it everywhere?",
            "Nothing is deleted, but items removed on one side while the other was offline come back. " +
            "Both copies are saved to Backup → Safety backups first.",
            "Yes, merge"),
        _ => new("Replace this device's bookmarks with the server copy?",
            "Edits on this device that are not yet synced are lost. A copy is saved to Backup → Safety backups first.",
            "Yes, download"),
    };

    // Move service.
    public const string MoveImplication =
        "The old sync stays on the old service and is not deleted. Your other devices stay linked to the old service " +
        "until you move them too. Store the new sync ID and password.";

    public static string MoveNewConfirm(string host, int count) =>
        $"Create a new sync on {host} and upload {count} bookmarks?";

    public const string MoveDoneNext = "Store the sync ID with the password, then move your other devices with it.";

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
