using Keepmark.Core.Logging;
using Keepmark.Core.Data;
using Keepmark.Core.Models;
namespace Keepmark.Core.Sync;
public sealed partial class SyncEngine
{
    public async Task<SelectionResult> ApplySelectionAsync(SelectionPlan plan, bool delete,
        Func<List<BookmarkNode>, string, Task> backup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (delete == plan.Moving) throw new ArgumentException("Selection operation does not match plan.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_resolvingConflict) throw new InvalidOperationException("Conflict resolution is in progress.");
            var validated = await store.PlanSelectionAsync(plan.RootNodeIds, plan.ContentVersion,
                plan.DestinationNodeId, plan.Moving).ConfigureAwait(false);
            if (validated.IsNoOp) return new(validated, false);
            var mirror = await store.LoadMirrorAsync(false).ConfigureAwait(false);
            if (store.ContentVersion != plan.ContentVersion) throw new SelectionChangedException();
            await backup(mirror.Roots, delete ? "selection-delete" : "selection-move").ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var result = await store.ApplySelectionAsync(validated, ct).ConfigureAwait(false);
            try { await NotifyLocalEditAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                log.Warn("sync", $"Selection committed; notification failed: {ex.GetType().Name}");
                return result with { NotificationFailed = true };
            }
            return result;
        }
        finally { _gate.Release(); }
    }
    public async Task UndoSelectionMoveAsync(SelectionMoveReceipt receipt,
        Func<List<BookmarkNode>, string, Task> backup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_resolvingConflict || store.ContentVersion != receipt.ContentVersion) throw new SelectionChangedException();
            var mirror = await store.LoadMirrorAsync(false).ConfigureAwait(false);
            if (store.ContentVersion != receipt.ContentVersion) throw new SelectionChangedException();
            await backup(mirror.Roots, "selection-move-undo").ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await store.UndoSelectionMoveAsync(receipt, ct).ConfigureAwait(false);
            try { await NotifyLocalEditAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.Warn("sync", $"Selection undo committed; notification failed: {ex.GetType().Name}"); }
        }
        finally { _gate.Release(); }
    }
    public async Task<(int RowId, long ContentVersion)> CreateSelectionFolderAsync(IEnumerable<int> ids,
        long expectedVersion, int? parentNodeId, string title, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireCredsAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_resolvingConflict) throw new SelectionChangedException();
            var created = await store.CreateSelectionFolderAsync(ids, expectedVersion, parentNodeId, title, ct).ConfigureAwait(false);
            try { await NotifyLocalEditAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.Warn("sync", $"Selection folder committed; notification failed: {ex.GetType().Name}"); }
            return created;
        }
        finally { _gate.Release(); }
    }
}
