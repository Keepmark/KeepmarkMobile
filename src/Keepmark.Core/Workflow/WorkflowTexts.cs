using Keepmark.Core.I18n;

namespace Keepmark.Core.Workflow;

/// <summary>
/// User-facing workflow texts: confirmations, implication lines and results.
/// One place to read and change them; wording follows docs/UI-TERMS.md and
/// docs/WORKFLOWS.md, kept identical to the browser extension
/// (KeepmarkApp src/popup/texts.ts) except "device" for "browser".
/// </summary>
public static class WorkflowTexts
{
    // Every text reads the active UI language at call time (Loc): none of
    // these may be a const or a static readonly field.

    // Change password.
    public static string ChangePasswordImplication => Loc.Get(Str.WfChangePasswordImplication);

    /// <summary>The browser adds "Undo is in Status"; the mobile app has no
    /// undo for it, the safety backup is the way back.</summary>
    public static string ChangePasswordDone => Loc.Get(Str.WfChangePasswordDone);

    // Connect → Unlink.
    public static string UnlinkImplication => Loc.Get(Str.WfUnlinkImplication);

    public static string UnlinkDone => Loc.Get(Str.WfUnlinkDone);

    // Connect → new sync.
    public static string CreatedNext => Loc.Get(Str.WfCreatedNext);

    public static string CreateEmptyImplication => Loc.Get(Str.WfCreateEmptyImplication);

    // Direction choice (Connect, Move, Conflict): picker order and confirms.
    /// <summary>Keep the server copy / Merge both / Keep this device's copy.</summary>
    public static readonly string[] DirectionOrder = ["download", "merge", "upload"];

    public sealed record Confirm(string Message, string Implication, string ConfirmLabel);

    public static Confirm DirectionConfirm(string direction) => direction switch
    {
        "upload" => new(Loc.Get(Str.WfDirUploadMessage), Loc.Get(Str.WfDirUploadImplication), Loc.Get(Str.CommonYesUpload)),
        "merge" => new(Loc.Get(Str.WfDirMergeMessage), Loc.Get(Str.WfDirMergeImplication), Loc.Get(Str.CommonYesMerge)),
        _ => new(Loc.Get(Str.WfDirDownloadMessage), Loc.Get(Str.WfDirDownloadImplication), Loc.Get(Str.CommonYesDownload)),
    };

    // Move service.
    public static string MoveImplication => Loc.Get(Str.WfMoveImplication);

    public static string MoveNewConfirm(string host, int count) => Loc.Plural(Str.WfMoveNewConfirm, count, host);

    public static string MoveDoneNext => Loc.Get(Str.WfMoveDoneNext);

    // Backup → Export.
    public static string BackupPasswordHint => Loc.Get(Str.WfBackupPasswordHint);

    public static string ExportDone(int count) => Loc.Plural(Str.WfExportDone, count);

    // Backup → Restore from a backup file.
    public static string ImportChecked(string fileName, int count) => Loc.Plural(Str.WfImportChecked, count, fileName);

    /// <summary>Unlinked, the mobile app restores this device only; linked, the
    /// restored tree is uploaded like any local edit.</summary>
    public static string ImportImplication(bool linked) =>
        Loc.Get(linked ? Str.WfImportImplicationLinked : Str.WfImportImplicationLocal);

    public static string ImportDone(int count, bool linked) =>
        Loc.Plural(linked ? Str.WfImportDoneLinked : Str.WfImportDoneLocal, count);

    // Backup → Safety backups.
    public static string SafetyRestoreImplication(string side, int items) =>
        Loc.Plural(side == "local" ? Str.WfSafetyRestoreLocal : Str.WfSafetyRestoreRemote, items);

    public static string SafetyRestoreDone(int count) => Loc.Plural(Str.WfSafetyRestoreDone, count);

    public static string SafetyDeleteImplication => Loc.Get(Str.WfSafetyDeleteImplication);

    // Logs (mobile only: the extension has no log clear).
    public static string LogsClearImplication => Loc.Get(Str.WfLogsClearImplication);

    // Duplicates.
    public static Confirm DupDeleteConfirm(int count) => new(
        Loc.Plural(Str.WfDupDeleteMessage, count),
        Loc.Get(Str.WfDupDeleteImplication),
        Loc.Get(Str.CommonYesDelete));

    public static string DupDeleteDone(int count) => Loc.Plural(Str.WfDupDeleteDone, count);
}
