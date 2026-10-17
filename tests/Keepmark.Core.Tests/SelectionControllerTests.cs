using Keepmark.Core.Data;
using KeepmarkMobile.Services;

namespace Keepmark.Core.Tests;

public sealed class SelectionControllerTests
{
    private static ItemRow Row(int id, int? parent = null, bool folder = false, string? url = null) =>
        new() { RowId = id, NodeId = id, ParentRowId = parent, IsFolder = folder, Url = folder ? null : url ?? "https://example.test" };

    [Fact]
    public void FolderSelectionCoversChildrenAndDoesNotRestoreOldSelections()
    {
        var folder = Row(1, folder: true);
        var child = Row(2, 1);
        var state = new SelectionController();
        state.Enter([child, folder], [folder, child], 12);
        state.Toggle(2);
        state.Toggle(1);
        Assert.Equal([1], state.SelectedNodeIds);
        Assert.True(state.IsCovered(2));
        state.Toggle(2);
        Assert.Equal([1], state.SelectedNodeIds);
        state.Toggle(1);
        Assert.Empty(state.SelectedNodeIds);
        Assert.True(state.Active);
    }

    [Fact]
    public void SelectAllNormalizesAncestorsDeduplicatesAliasesAndPreservesViewOrder()
    {
        var folder = Row(1, folder: true);
        var child = Row(2, 1, url: "https://same");
        var separate = Row(3, url: "https://same");
        var state = new SelectionController();
        state.Enter([child, separate, folder, separate], [folder, child, separate], 1);
        state.SelectAll();
        Assert.Equal([3, 1], state.SelectedNodeIds);
        Assert.True(state.AllSelected);
        state.SelectNone();
        Assert.Empty(state.SelectedNodeIds);
        Assert.True(state.Active);
    }

    [Fact]
    public void RejectsAmbiguousLegacyProtectedAndSeparatorRows()
    {
        var root = Row(1, folder: true); root.Title = "[xbs] Menu";
        var nested = Row(2, 1, true); nested.Title = "[xbs] Menu";
        var legacy = Row(3); legacy.NodeId = null;
        var ambiguous = Row(4);
        var duplicate = Row(5); duplicate.NodeId = 4;
        var separator = Row(6, url: XbsConstants.SeparatorUrl);
        ItemRow[] tree = [root, nested, legacy, ambiguous, duplicate, separator];
        var state = new SelectionController();
        state.Enter(tree, tree, 1);
        state.SelectAll();
        Assert.Equal([2], state.SelectedNodeIds);
    }
}
