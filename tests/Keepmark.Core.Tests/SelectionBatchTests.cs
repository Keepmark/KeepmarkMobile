using Keepmark.Core.Sync;
using Keepmark.Core.Data;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
namespace Keepmark.Core.Tests;
public sealed class SelectionBatchTests : IAsyncLifetime
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"selection-{Guid.NewGuid():N}.db");
    BookmarkStore store = null!;
    public async Task InitializeAsync()
    {
        store = new(path, new string('a', 64), new InMemoryLogger());
        await store.InitializeAsync();
        await store.RestoreLocalAsync([
            new() { Id=1, Title="A", Children=[new() { Id=2, Title="One", Url="https://one" },
                new() { Id=3, Title="B", Children=[new() { Id=4, Title="Two", Url="https://two" }] }] },
            new() { Id=5, Title="C", Children=[] },
            new() { Id=6, Title="Three", Url="https://three" }
        ]);
    }
    public async Task DisposeAsync() { await store.DisposeAsync(); File.Delete(path); }
    [Fact]
    public async Task OverlappingDeleteCountsOnceAndDeletesWholeSubtree()
    {
        var plan = await store.PlanSelectionAsync([1,3,4], store.ContentVersion);
        Assert.Equal([1], plan.RootNodeIds);
        Assert.Equal(2, plan.Folders); Assert.Equal(2, plan.Bookmarks);
        await store.ApplySelectionAsync(plan);
        var mirror = await store.LoadMirrorAsync(false);
        Assert.Equal([5,6], mirror.Roots.Select(x=>x.Id));
    }
    [Fact]
    public async Task MovePreservesCapturedOrderAndUndoRestoresOriginalPositions()
    {
        var before = System.Text.Json.JsonSerializer.Serialize((await store.LoadMirrorAsync(false)).Roots);
        var plan = await store.PlanSelectionAsync([6,2], store.ContentVersion,5,true);
        var result = await store.ApplySelectionAsync(plan);
        var mirror = await store.LoadMirrorAsync(false);
        Assert.Equal([6,2], mirror.Roots.Single(x=>x.Id==5).Children!.Select(x=>x.Id));
        await store.UndoSelectionMoveAsync(result.Undo!);
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize((await store.LoadMirrorAsync(false)).Roots));
    }
    [Fact]
    public async Task NoOpDoesNotChangeVersionAndMissingDestinationNeverFallsBack()
    {
        var version=store.ContentVersion;
        var plan=await store.PlanSelectionAsync([2],version,1,true);
        Assert.False((await store.ApplySelectionAsync(plan)).Applied);
        Assert.Equal(version,store.ContentVersion);
        await Assert.ThrowsAsync<SelectionChangedException>(()=>store.PlanSelectionAsync([2],version,999,true));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.PlanSelectionAsync([1],version,3,true));
        Assert.Equal(version,store.ContentVersion);
    }
    [Fact]
    public async Task StaleApplyAndUndoFailWithoutMutation()
    {
        var plan=await store.PlanSelectionAsync([2],store.ContentVersion,5,true);
        await store.UpdateNodeByNodeIdAsync(6,"Edited","https://three",null,null);
        await Assert.ThrowsAsync<SelectionChangedException>(()=>store.ApplySelectionAsync(plan));
        var result=await store.ApplySelectionAsync(await store.PlanSelectionAsync([2],store.ContentVersion,5,true));
        await store.UpdateNodeByNodeIdAsync(6,"Edited again","https://three",null,null);
        await Assert.ThrowsAsync<SelectionChangedException>(()=>store.UndoSelectionMoveAsync(result.Undo!));
        Assert.Equal(2,(await store.LoadMirrorAsync(false)).Roots.Single(x=>x.Id==5).Children!.Single().Id);
    }
    [Fact]
    public async Task SelectionFolderCreationRefreshesVersionAndRejectsStaleOrMissingParents()
    {
        var version=store.ContentVersion;
        var row=await store.CreateSelectionFolderAsync([2],version,5,"New");
        Assert.True(row.RowId>0); Assert.Equal(version+1,row.ContentVersion); Assert.Equal(version+1,store.ContentVersion);
        await Assert.ThrowsAsync<SelectionChangedException>(()=>store.CreateSelectionFolderAsync([2],version,5,"Stale"));
        await Assert.ThrowsAsync<SelectionChangedException>(()=>store.CreateSelectionFolderAsync([2],store.ContentVersion,999,"Lost"));
        Assert.Single((await store.LoadMirrorAsync(false)).Roots.Single(x=>x.Id==5).Children!);
    }
    [Fact]
    public async Task LargeDeleteDoesNotExceedParameterLimit()
    {
        await store.RestoreLocalAsync(Enumerable.Range(1,1500).Select(id=>new BookmarkNode { Id=id,Title=$"Link {id}",Url=$"https://{id}" }).ToList());
        var plan=await store.PlanSelectionAsync(Enumerable.Range(1,1500),store.ContentVersion);
        await store.ApplySelectionAsync(plan);
        Assert.Empty((await store.LoadMirrorAsync(false)).Roots);
    }
    [Fact]
    public async Task BackupFailureAndConcurrentEditDuringBackupFailClosed()
    {
        var logger = new InMemoryLogger();
        var engine = new Keepmark.Core.Sync.SyncEngine(_ => new FakeApi(),
            new Keepmark.Core.Crypto.XCryptoService(new Keepmark.Core.I18n.XbsLocalizer(), logger),
            store, new MemCreds(new SyncCredentials("sync", "https://example.test",
                Convert.ToBase64String(new byte[32]), "1.8.0")),
            new Keepmark.Core.I18n.XbsLocalizer(), logger) { Mode = Keepmark.Core.Sync.SyncMode.Manual };
        var plan = await store.PlanSelectionAsync([2], store.ContentVersion, 5, true);
        var version = store.ContentVersion;
        await Assert.ThrowsAsync<IOException>(() => engine.ApplySelectionAsync(plan, false,
            (_, _) => throw new IOException("Backup failed")));
        Assert.Equal(version, store.ContentVersion);
        await Assert.ThrowsAsync<SelectionChangedException>(() => engine.ApplySelectionAsync(plan, false,
            async (_, _) => await store.UpdateNodeByNodeIdAsync(6,"Changed","https://three",null,null)));
        Assert.Empty((await store.LoadMirrorAsync(false)).Roots.Single(x=>x.Id==5).Children!);
        int backups=0;
        var fresh=await store.PlanSelectionAsync([2,6],store.ContentVersion,5,true);
        var result=await engine.ApplySelectionAsync(fresh,false,(_,operation)=>
        { Assert.Equal("selection-move",operation); backups++; return Task.CompletedTask; });
        Assert.True(result.Applied); Assert.Equal(1,backups);
    }

    [Fact]
    public async Task SeparatorsAndProtectedContainersCannotBeRootsButNestedContentsAreCounted()
    {
        await store.RestoreLocalAsync([
            new() { Id=1,Title=ContainerNames.Menu,Children=[] },
            new() { Id=2,Title="[xbs] Custom",Children=[new() { Id=3,Url=Keepmark.Core.XbsConstants.SeparatorUrl }] }
        ]);
        var version=store.ContentVersion;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.PlanSelectionAsync([1],version));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.PlanSelectionAsync([3],version));
        var plan=await store.PlanSelectionAsync([2],version);
        Assert.Equal(1,plan.RootFolders); Assert.Equal(0,plan.ContainedFolders);
        Assert.Equal(0,plan.Bookmarks); Assert.Equal(1,plan.Separators);
        await store.ApplySelectionAsync(plan);
        Assert.Single((await store.LoadMirrorAsync(false)).Roots);
    }

    [Fact]
    public async Task CancelledMutationsLeaveContentsAndVersionUntouched()
    {
        var version=store.ContentVersion;
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        var plan=await store.PlanSelectionAsync([2],version,5,true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.ApplySelectionAsync(plan,cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.CreateSelectionFolderAsync([2],version,5,"No",cancelled.Token));
        Assert.Equal(version,store.ContentVersion);
        Assert.Empty((await store.LoadMirrorAsync(false)).Roots.Single(x=>x.Id==5).Children!);
    }

}
