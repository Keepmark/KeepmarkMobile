using Keepmark.Core.Data;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Sync;
using KeepmarkMobile.Services;
using Microsoft.JSInterop;

namespace KeepmarkMobile.Components.Pages;

public partial class Home
{
    private readonly SelectionController selection = new();
    private long renderedContentVersion = -1;
    private int? selectionFocusNodeId;
    private bool restoreSelectionFocus;
    private int selectionWork;
    private bool selectionRefreshPending;
    private bool batchMoving;
    private CancellationTokenSource? selectionOperation;
    private SelectionPlan? batchDeletePlan;
    private SelectionMoveReceipt? batchUndo;
    private CancellationTokenSource? batchUndoExpiry;
    private bool batchUndoBusy;
    private string? selectionError, batchError, batchResultText;

    private IEnumerable<ItemRow> SelectionCandidates => isSearch ? hitRows.Select(id => byRow[id])
        : recentView ? recentAll
        : (crumbs.Count == 0 ? (pinnedOpen ? pinnedRows : []).Concat(showRecent && recentOpen ? RecentShown : []) : [])
            .Concat(rows);
    private bool AllSelected => selection.AllSelected;
    private IReadOnlyCollection<int> BatchExcludedNodes => allFolders.Where(IsBatchExcluded)
        .Where(f => f.NodeId.HasValue).Select(f => f.NodeId!.Value).ToArray();
    private bool IsBatchExcluded(ItemRow folder)
    {
        var seen = new HashSet<int>();
        ItemRow? current = folder;
        while (current is not null && seen.Add(current.RowId))
        {
            if (selection.IsSelected(current.NodeId)) return true;
            current = current.ParentRowId is int parent ? allFolders.FirstOrDefault(f => f.RowId == parent) : null;
        }
        return false;
    }
    private IReadOnlyCollection<int?> BatchSourceParents => SelectionCandidates.Where(r => selection.IsSelected(r.NodeId))
        .DistinctBy(r => r.NodeId).Select(r => r.ParentRowId).ToArray();

    private async Task BeginSelection(ItemRow? row)
    {
        if (row?.NodeId is not int id || selection.Active || selection.Busy || selectionWork > 0 || searching || sheetFor is not null || movingFor is not null || confirmDelete is not null || Session.Store is null) return;
        if (renderedContentVersion < 0 || renderedContentVersion != Session.Store.ContentVersion)
        {
            await RefreshAsync(); selectionError = L.Get(Str.SelectionChanged); return;
        }
        selection.Enter(SelectionCandidates, allFolders.Concat(byRow.Values).DistinctBy(r => r.RowId), renderedContentVersion);
        if (!selection.IsAvailable(id))
        {
            selection.Clear();
            selectionError = null;
            // Long press arrives through JS interop: notify the layout immediately,
            // rather than leaving an off-screen error until the next navigation.
            _ = Toast.ShowAsync(L.Get(Str.SelectionUnavailable));
            StateHasChanged();
            return;
        }
        selection.Toggle(id);
        selectionFocusNodeId = id;
        menuFor = null; sortMenuOpen = false; expandedRows.Clear();
        debounce?.Cancel(); searchGen++; focusSearch = false;
        selectionError = batchError = null;
        undo = null; moved = null; undoTimer?.Cancel();
        batchUndo = null; batchUndoExpiry?.Cancel();
        await JS.InvokeVoidAsync("xbsGestures.blurActive");
        StateHasChanged();
    }

    private void ToggleSelection(ItemRow row)
    {
        if (!selection.Active || selection.Busy) return;
        if (Session.Store?.ContentVersion != selection.ContentVersion) { InvalidateSelection(); return; }
        if (row.NodeId is int id) selection.Toggle(id);
    }

    private void ToggleAllSelection()
    {
        if (selection.Busy) return;
        if (Session.Store?.ContentVersion != selection.ContentVersion) { InvalidateSelection(); return; }
        if (AllSelected) selection.SelectNone(); else selection.SelectAll();
    }

    private void ExitSelection()
    {
        if (selection.Busy) return;
        restoreSelectionFocus = selection.Active;
        selection.Clear(); batchMoving = false; batchDeletePlan = null; batchError = null;
    }
    private void SuspendSelection()
    {
        selectionOperation?.Cancel();
        selection.Clear(); batchMoving = false; batchDeletePlan = null; batchError = null;
    }
    private void OnSelectionLockChanged() => _ = InvokeAsync(() =>
    {
        if (AppLock.Locked || !AppLock.Foreground) { SuspendSelection(); StateHasChanged(); }
    });
    private CancellationToken StartSelectionOperation()
    {
        selectionOperation?.Dispose();
        selectionOperation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        return selectionOperation.Token;
    }
    private void InvalidateSelection()
    {
        ExitSelection(); selectionError = L.Get(Str.SelectionChanged);
    }
    private void CloseBatchMove() { if (!selection.Busy) { batchMoving = false; batchError = null; } }
    private void CloseBatchDelete() { if (!selection.Busy) { batchDeletePlan = null; batchError = null; } }
    private void OpenBatchMove()
    {
        if (selection.Busy || selection.SelectedNodeIds.Count == 0) return;
        if (Session.Store?.ContentVersion != selection.ContentVersion) { InvalidateSelection(); return; }
        batchError = selectionError = null; batchMoving = true;
    }
    private async Task PrepareBatchDelete()
    {
        if (Session.Store is null || selection.Busy) return;
        var generation = selection.Generation;
        selectionWork++;
        selection.Busy = true; selectionError = null;
        try
        {
            var plan = await Session.Store.PlanSelectionAsync(selection.SelectedNodeIds, selection.ContentVersion);
            if (!IsDisposed && selection.Active && generation == selection.Generation) batchDeletePlan = plan;
        }
        catch (Exception ex) { HandleBatchError(ex); }
        finally { await FinishSelectionOperation(); }
    }
    private async Task ApplyBatchDelete()
    {
        if (batchDeletePlan is null) return;
        await ApplyBatch(batchDeletePlan, true);
    }
    private async Task ApplyBatchMove(int? targetRowId)
    {
        if (Session.Store is null || selection.Busy) return;
        var generation = selection.Generation;
        selectionWork++;
        selection.Busy = true;
        try
        {
            int? nodeId = null;
            if (targetRowId is int rowId)
            {
                var target = allFolders.SingleOrDefault(f => f.RowId == rowId);
                nodeId = target?.NodeId ?? throw new InvalidOperationException(L.Get(Str.SelectionChanged));
            }
            var plan = await Session.Store.PlanSelectionAsync(selection.SelectedNodeIds, selection.ContentVersion, nodeId, moving: true);
            if (IsDisposed || !selection.Active || generation != selection.Generation) return;
            selection.Busy = false;
            await ApplyBatch(plan, false);
        }
        catch (Exception ex) { HandleBatchError(ex); }
        finally { await FinishSelectionOperation(); }
    }
    private async Task ApplyBatch(SelectionPlan plan, bool delete)
    {
        if (Session.Engine is null || selection.Busy) return;
        var movedRows = byRow.Values.Where(r => r.NodeId is int id && plan.MoveNodeIds.Contains(id)).ToArray();
        var movedFolders = movedRows.Count(r => r.IsFolder);
        var movedBookmarks = movedRows.Count(r => !r.IsFolder);
        var operationToken = StartSelectionOperation();
        selectionWork++;
        selection.Busy = true; batchError = selectionError = null;
        StateHasChanged();
        try
        {
            var result = await Session.Engine.ApplySelectionAsync(plan, delete, Session.SaveSelectionBackupAsync, operationToken);
            if (IsDisposed) return;
            selection.Busy = false;
            ExitSelection();
            if (!result.Applied) { await RefreshAsync(); return; }
            if (!delete && plan.DestinationNodeId is int destinationNodeId)
                RememberMoveTarget(destinationNodeId);
            var destinationPath = FolderPath(allFolders.FirstOrDefault(f => f.NodeId == plan.DestinationNodeId)?.RowId);
            await RefreshAsync();
            var text = delete ? L.Get(Str.SelectionDeleted, plan.RootFolders, plan.RootBookmarks) : string.Empty;
            if (!delete)
            {
                var counts = new List<string>();
                if (movedFolders > 0) counts.Add(Loc.Plural(Str.HomeCountFolders, movedFolders));
                if (movedBookmarks > 0) counts.Add(Loc.Plural(Str.HomeCountBookmarks, movedBookmarks));
                text = $"{string.Join(" · ", counts)} → {destinationPath}";
            }
            if (!delete && result.Undo is not null)
            {
                batchUndo = result.Undo;
                batchResultText = text;
                batchUndoExpiry?.Cancel(); batchUndoExpiry?.Dispose();
                var expiry = batchUndoExpiry = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
                _ = ExpireBatchUndo(expiry.Token);
            }
            StateHasChanged();
            if (delete) _ = Toast.ShowAsync(text + " · " + L.Get(Str.SelectionBackupAvailable), ms: 6500);
        }
        catch (Exception ex) { HandleBatchError(ex); }
        finally { await FinishSelectionOperation(); }
    }
    private async Task ExpireBatchUndo(CancellationToken ct)
    {
        try { await Task.Delay(6000, ct); await InvokeAsync(() => { batchUndo = null; StateHasChanged(); }); }
        catch (OperationCanceledException) { }
    }
    private async Task UndoBatchMove()
    {
        if (batchUndo is null || Session.Engine is null || batchUndoBusy) return;
        batchUndoBusy = true; batchUndoExpiry?.Cancel();
        var receipt = batchUndo; batchUndo = null;
        try { await Session.Engine.UndoSelectionMoveAsync(receipt, Session.SaveSelectionBackupAsync, Lifetime); await RefreshAsync(); }
        catch (Exception ex)
        {
            Session.Log.Warn("ui", $"Selection undo failed: {ex.GetType().Name}");
            selectionError = L.Get(Str.SelectionUndoFailed);
            _ = Toast.ShowAsync(selectionError, isError: true);
        }
        finally { batchUndoBusy = false; StateHasChanged(); }
    }
    private async Task<int?> CreateBatchFolder(int? parentRowId, string title)
    {
        if (Session.Engine is null || selection.Busy) return null;
        var generation = selection.Generation;
        var operationToken = StartSelectionOperation();
        selectionWork++;
        selection.Busy = true;
        try
        {
            int? parentId = null;
            if (parentRowId is int rowId) parentId = allFolders.SingleOrDefault(f => f.RowId == rowId)?.NodeId
                ?? throw new InvalidOperationException(L.Get(Str.SelectionChanged));
            var created = await Session.Engine.CreateSelectionFolderAsync(selection.SelectedNodeIds, selection.ContentVersion, parentId, title, operationToken);
            if (IsDisposed || !selection.Active || generation != selection.Generation) return null;
            selection.RebaseVersion(created.ContentVersion);
            await RefreshAsync();
            if (Session.Store?.ContentVersion != selection.ContentVersion) { selection.Busy = false; InvalidateSelection(); return null; }
            return created.RowId;
        }
        catch (Exception ex) { HandleBatchError(ex); return null; }
        finally { await FinishSelectionOperation(); }
    }
    private async Task FinishSelectionOperation()
    {
        selectionWork = Math.Max(0, selectionWork - 1);
        if (selectionWork > 0) return;
        selection.Busy = false;
        if (IsDisposed) return;
        if (selection.Active && Session.Store?.ContentVersion != selection.ContentVersion) InvalidateSelection();
        if (selectionRefreshPending)
        {
            selectionRefreshPending = false;
            await RefreshAsync();
        }
    }
    private void HandleBatchError(Exception ex)
    {
        if (IsDisposed || ex is OperationCanceledException || !selection.Active) return;
        Session.Log.Warn("ui", $"Selection operation failed: {ex.GetType().Name}");
        batchError = selectionError = Session.Store?.ContentVersion != selection.ContentVersion ? L.Get(Str.SelectionChanged) : L.Get(Str.SelectionFailed);
    }
}
