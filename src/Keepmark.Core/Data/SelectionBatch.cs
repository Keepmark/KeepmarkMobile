using SQLite;
using Keepmark.Core.Models;

namespace Keepmark.Core.Data;

public sealed class SelectionChangedException() : InvalidOperationException("Selection content changed.");
public sealed record SelectionPlan(long ContentVersion, IReadOnlyList<int> RootNodeIds,
    IReadOnlyList<int> AffectedNodeIds, int Folders, int Bookmarks, int Separators,
    int? DestinationNodeId, bool Moving, IReadOnlyList<int> MoveNodeIds)
{
    public int RootFolders { get; init; }
    public int RootBookmarks { get; init; }
    public int ContainedFolders => Folders - RootFolders;
    public int ContainedBookmarks => Bookmarks - RootBookmarks;
    public int Total => Folders + Bookmarks + Separators;
    public bool IsNoOp => RootNodeIds.Count == 0 || (Moving && MoveNodeIds.Count == 0);
}
public sealed record SelectionPosition(int NodeId, int? ParentNodeId, int SortOrder);
public sealed record SelectionMoveReceipt(long ContentVersion, IReadOnlyList<SelectionPosition> OriginalPositions);
public sealed record SelectionResult(SelectionPlan Plan, bool Applied, SelectionMoveReceipt? Undo = null,
    bool NotificationFailed = false);

public sealed partial class BookmarkStore
{
    private void RequireSelectionVersion(long expected)
    {
        if (ContentVersion != expected) throw new SelectionChangedException();
    }

    private SelectionPlan BuildSelectionPlan(SQLiteConnection tran, IEnumerable<int> ids,
        long expected, int? destination, bool moving)
    {
        RequireSelectionVersion(expected);
        var rows = tran.Table<ItemRow>().ToList();
        var byRow = rows.ToDictionary(x => x.RowId);
        var byNode = rows.Where(x => x.NodeId.HasValue).GroupBy(x => x.NodeId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        ItemRow Resolve(int id) => byNode.TryGetValue(id, out var found) && found.Count == 1
            ? found[0] : throw new SelectionChangedException();
        var selected = ids.Distinct().Select(Resolve).ToList();
        if (selected.Any(x => (x.IsFolder && x.ParentRowId is null && x.Title is ContainerNames.Menu or ContainerNames.Other or ContainerNames.Toolbar)
            || (!x.IsFolder && (string.IsNullOrEmpty(x.Url) || x.Url == XbsConstants.SeparatorUrl))))
            throw new InvalidOperationException("Protected items cannot be selected.");
        var selectedRows = selected.Select(x => x.RowId).ToHashSet();
        bool HasSelectedAncestor(ItemRow row)
        {
            var seen = new HashSet<int>();
            while (row.ParentRowId is int parent)
            {
                if (!seen.Add(parent) || !byRow.TryGetValue(parent, out row!))
                    throw new InvalidOperationException("Invalid bookmark tree.");
                if (selectedRows.Contains(parent)) return true;
            }
            return false;
        }
        // Caller order is the captured visual/context order, never tap order.
        var roots = selected.Where(x => !HasSelectedAncestor(x)).ToList();
        var children = rows.Where(x => x.ParentRowId.HasValue).GroupBy(x => x.ParentRowId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var affected = new List<ItemRow>();
        var visited = new HashSet<int>();
        var queue = new Queue<ItemRow>(roots);
        while (queue.TryDequeue(out var row))
        {
            if (!visited.Add(row.RowId)) throw new InvalidOperationException("Invalid bookmark tree.");
            affected.Add(row);
            if (children.TryGetValue(row.RowId, out var kids)) foreach (var child in kids) queue.Enqueue(child);
        }
        int? parentRow = null;
        if (moving && destination.HasValue)
        {
            var target = Resolve(destination.Value);
            if (!target.IsFolder || visited.Contains(target.RowId))
                throw new InvalidOperationException("Invalid move destination.");
            parentRow = target.RowId;
        }
        return new(expected, roots.Select(x => x.NodeId!.Value).ToArray(),
            affected.Where(x => x.NodeId.HasValue).Select(x => x.NodeId!.Value).ToArray(),
            affected.Count(x => x.IsFolder), affected.Count(x => !x.IsFolder && !string.IsNullOrEmpty(x.Url) && x.Url != XbsConstants.SeparatorUrl),
            affected.Count(x => !x.IsFolder && (string.IsNullOrEmpty(x.Url) || x.Url == XbsConstants.SeparatorUrl)), destination, moving,
            moving ? roots.Where(x => x.ParentRowId != parentRow).Select(x => x.NodeId!.Value).ToArray() : [])
            { RootFolders = roots.Count(x => x.IsFolder), RootBookmarks = roots.Count(x => !x.IsFolder) };
    }

    public async Task<SelectionPlan> PlanSelectionAsync(IEnumerable<int> ids, long expectedVersion,
        int? destinationNodeId = null, bool moving = false)
    {
        var captured = ids.ToArray();
        SelectionPlan? plan = null;
        await Db.RunInTransactionAsync(tran => plan = BuildSelectionPlan(tran, captured,
            expectedVersion, destinationNodeId, moving)).ConfigureAwait(false);
        return plan!;
    }

    public async Task<SelectionResult> ApplySelectionAsync(SelectionPlan supplied, CancellationToken ct = default)
    {
        SelectionResult? result = null;
        await Db.RunInTransactionAsync(tran =>
        {
            ct.ThrowIfCancellationRequested();
            var plan = BuildSelectionPlan(tran, supplied.RootNodeIds, supplied.ContentVersion,
                supplied.DestinationNodeId, supplied.Moving);
            if (plan.IsNoOp) { result = new(plan, false); return; }
            var rows = tran.Table<ItemRow>().ToList();
            var byRow = rows.ToDictionary(x => x.RowId);
            var byNode = rows.Where(x => x.NodeId.HasValue).GroupBy(x => x.NodeId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());
            ItemRow Resolve(int id) => byNode.TryGetValue(id, out var found) && found.Count == 1
                ? found[0] : throw new SelectionChangedException();
            List<SelectionPosition> positions = [];
            if (plan.Moving)
            {
                int? parent = plan.DestinationNodeId is int id ? Resolve(id).RowId : null;
                int order = rows.Where(x => x.ParentRowId == parent).Select(x => x.SortOrder).DefaultIfEmpty(-1).Max() + 1;
                foreach (var idToMove in plan.MoveNodeIds)
                {
                    var row = Resolve(idToMove);
                    var originalParent = row.ParentRowId is int p ? byRow[p].NodeId : null;
                    if (row.ParentRowId.HasValue && !originalParent.HasValue) throw new SelectionChangedException();
                    positions.Add(new(row.NodeId!.Value, originalParent, row.SortOrder));
                    row.ParentRowId = parent; row.SortOrder = order++; tran.Update(row);
                }
            }
            else
            {
                var roots = plan.RootNodeIds.Select(x => Resolve(x).RowId);
                DeleteRows(tran, LoadSubtrees(tran, roots).Select(x => x.RowId));
            }
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
            // Publish only after every SQL statement succeeded. SQLite serializes all store transactions.
            var version = Interlocked.Increment(ref _contentVersion);
            result = new(plan, true, plan.Moving ? new(version, positions) : null);
        }).ConfigureAwait(false);
        return result!;
    }

    public async Task UndoSelectionMoveAsync(SelectionMoveReceipt receipt, CancellationToken ct = default)
    {
        await Db.RunInTransactionAsync(tran =>
        {
            ct.ThrowIfCancellationRequested();
            RequireSelectionVersion(receipt.ContentVersion);
            var groups = tran.Table<ItemRow>().ToList().Where(x => x.NodeId.HasValue)
                .GroupBy(x => x.NodeId!.Value).ToDictionary(g => g.Key, g => g.ToList());
            ItemRow Resolve(int id) => groups.TryGetValue(id, out var found) && found.Count == 1
                ? found[0] : throw new SelectionChangedException();
            var updates = receipt.OriginalPositions.Select(p => (Row: Resolve(p.NodeId), Position: p,
                Parent: p.ParentNodeId is int parent ? Resolve(parent) : null)).ToList();
            if (updates.Any(x => x.Parent is not null && !x.Parent.IsFolder)) throw new SelectionChangedException();
            foreach (var update in updates)
            {
                update.Row.ParentRowId = update.Parent?.RowId;
                update.Row.SortOrder = update.Position.SortOrder;
                tran.Update(update.Row);
            }
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
            Interlocked.Increment(ref _contentVersion);
        }).ConfigureAwait(false);
    }
    public async Task<(int RowId, long ContentVersion)> CreateSelectionFolderAsync(IEnumerable<int> ids, long expectedVersion,
        int? parentNodeId, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Folder name is required.");
        var captured = ids.ToArray();
        int rowId = 0;
        long createdVersion = 0;
        await Db.RunInTransactionAsync(tran =>
        {
            ct.ThrowIfCancellationRequested();
            BuildSelectionPlan(tran, captured, expectedVersion, parentNodeId, true);
            int? parentRow = parentNodeId is int id
                ? tran.Query<ItemRow>("SELECT * FROM Items WHERE NodeId = ?", id).Single().RowId : null;
            int next = (tran.ExecuteScalar<int?>("SELECT MAX(NodeId) FROM Items") ?? 0) + 1;
            int order = tran.Table<ItemRow>().ToList().Where(x => x.ParentRowId == parentRow)
                .Select(x => x.SortOrder).DefaultIfEmpty(-1).Max() + 1;
            var node = new Keepmark.Core.Models.BookmarkNode { Id = next, Title = title.Trim(), Children = [],
                DateAdded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            InsertNode(tran, node, parentRow, ref order);
            rowId = tran.ExecuteScalar<int>("SELECT RowId FROM Items WHERE NodeId = ?", next);
            tran.InsertOrReplace(new MetaRow { Key = MetaDirty, Value = "1" });
            createdVersion = Interlocked.Increment(ref _contentVersion);
        }).ConfigureAwait(false);
        return (rowId, createdVersion);
    }

}
