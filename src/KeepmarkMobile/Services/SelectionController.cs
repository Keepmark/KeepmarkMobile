using Keepmark.Core;
using Keepmark.Core.Data;

namespace KeepmarkMobile.Services;

/// <summary>Transient selection over one immutable view of a content version.</summary>
public sealed class SelectionController
{
    private readonly List<int> order = [];
    private readonly Dictionary<int, int[]> ancestors = [];
    private readonly HashSet<int> selected = [];

    public bool Active { get; private set; }
    public bool Busy { get; set; }
    public long ContentVersion { get; private set; }
    public long Generation { get; private set; }
    public int Count => selected.Count;
    public IReadOnlyList<int> CandidateNodeIds => order.ToArray();
    public IReadOnlyList<int> SelectedNodeIds => order.Where(selected.Contains).ToArray();
    public bool AllSelected => order.Count > 0 && order.All(id => selected.Contains(id) || IsCovered(id));

    public void Enter(IEnumerable<ItemRow> candidates, IEnumerable<ItemRow> tree, long contentVersion)
    {
        Clear();
        var rows = tree.ToArray();
        var byRow = rows.GroupBy(r => r.RowId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var byNode = rows.Where(r => r.NodeId.HasValue).GroupBy(r => r.NodeId!.Value)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        foreach (var candidate in candidates)
        {
            if (candidate.NodeId is not int id || ancestors.ContainsKey(id) || !byNode.TryGetValue(id, out var row)) continue;
            if ((!row.IsFolder && (string.IsNullOrEmpty(row.Url) || row.Url == XbsConstants.SeparatorUrl)) ||
                (row.IsFolder && row.ParentRowId is null && row.Title is "[xbs] Menu" or "[xbs] Other" or "[xbs] Toolbar")) continue;
            var chain = new List<int>();
            var seen = new HashSet<int> { row.RowId };
            var parent = row.ParentRowId;
            var valid = true;
            while (parent is int parentId)
            {
                if (!seen.Add(parentId) || !byRow.TryGetValue(parentId, out var ancestor)) { valid = false; break; }
                if (ancestor.NodeId is int ancestorId) chain.Add(ancestorId);
                parent = ancestor.ParentRowId;
            }
            if (!valid) continue;
            ancestors[id] = chain.ToArray();
            order.Add(id);
        }
        ContentVersion = contentVersion;
        Active = true;
    }

    public bool IsAvailable(int? nodeId) => nodeId is int id && ancestors.ContainsKey(id);
    public bool IsSelected(int? nodeId) => nodeId is int id && selected.Contains(id);
    public bool IsCovered(int? nodeId) => nodeId is int id && ancestors.TryGetValue(id, out var chain) && chain.Any(selected.Contains);

    public void Toggle(int nodeId)
    {
        if (!Active || Busy || !IsAvailable(nodeId) || IsCovered(nodeId)) return;
        if (!selected.Remove(nodeId))
        {
            selected.RemoveWhere(id => ancestors[id].Contains(nodeId));
            selected.Add(nodeId);
        }
    }

    public void SelectAll()
    {
        if (!Active || Busy) return;
        selected.Clear();
        // Parent candidates win even when search order places their children first.
        var candidateSet = order.ToHashSet();
        foreach (var id in order)
            if (!ancestors[id].Any(candidateSet.Contains)) selected.Add(id);
    }

    public void SelectNone() { if (!Busy) selected.Clear(); }

    /// <summary>Only for an explicitly coordinated local mutation, such as creating a picker destination.</summary>
    public void RebaseVersion(long version) { ContentVersion = version; Generation++; }

    public void Clear()
    {
        Active = false;
        Busy = false;
        selected.Clear();
        order.Clear();
        ancestors.Clear();
        Generation++;
    }
}
