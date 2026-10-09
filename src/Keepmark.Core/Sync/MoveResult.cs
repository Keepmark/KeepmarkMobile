// Contextual wizard success: restates WHAT ran (mode + direction + target),
// since connect/switch wizards have four different outcomes. Pure for
// testability; callers append IDs + copy buttons. Port of KeepmarkApp
// describeMoveResult (src/popup/ui.ts).
using Keepmark.Core.I18n;

namespace Keepmark.Core.Sync;

public static class MoveResult
{
    /// <param name="mode">"new" (created sync) or "existing" (linked sync).</param>
    /// <param name="direction">"push", "pull" or "merge".</param>
    public static string Describe(string mode, string direction, string serviceUrl, string? syncId) =>
        (mode, direction) switch
        {
            ("new", _) => Loc.Get(Str.MoveResultNew, serviceUrl),
            (_, "push") => Loc.Get(Str.MoveResultPush, serviceUrl, syncId),
            (_, "merge") => Loc.Get(Str.MoveResultMerge, serviceUrl, syncId),
            _ => Loc.Get(Str.MoveResultPull, serviceUrl, syncId),
        };
}
