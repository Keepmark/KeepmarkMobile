// Contextual wizard success: restates WHAT ran (mode + direction + target),
// since connect/switch wizards have four different outcomes. Pure for
// testability; callers append IDs + copy buttons. Port of KeepmarkApp
// describeMoveResult (src/popup/ui.ts).
namespace Keepmark.Core.Sync;

public static class MoveResult
{
    /// <param name="mode">"new" (created sync) or "existing" (linked sync).</param>
    /// <param name="direction">"push", "pull" or "merge".</param>
    public static string Describe(string mode, string direction, string serviceUrl, string? syncId) =>
        (mode, direction) switch
        {
            ("new", _) =>
                $"Created a new sync on {serviceUrl} and uploaded your bookmarks.",
            (_, "push") =>
                $"Overwrote the remote sync {syncId} on {serviceUrl} with your local bookmarks.",
            (_, "merge") =>
                $"Merged your local bookmarks with the remote sync {syncId} on {serviceUrl} (nothing deleted).",
            _ =>
                $"Replaced your local bookmarks with the remote sync {syncId} from {serviceUrl}.",
        };
}
