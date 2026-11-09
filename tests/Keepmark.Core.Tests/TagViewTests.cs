using Keepmark.Core.Data;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;

namespace Keepmark.Core.Tests;

public sealed class TagViewTests : IAsyncLifetime
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"tags-{Guid.NewGuid():N}.db");
    BookmarkStore store = null!;

    public async Task InitializeAsync()
    {
        store = new(path, new string('a', 64), new InMemoryLogger());
        await store.InitializeAsync();
        await store.RestoreLocalAsync([
            new() { Id = 1, Title = "Folder", Children = [
                new() { Id = 2, Title = "One", Url = "https://one", Tags = ["work", "News"] },
                new() { Id = 3, Title = "Two", Url = "https://two", Tags = ["Work", "dev"] },
                new() { Id = 4, Url = XbsConstants.SeparatorUrl },
            ] },
            new() { Id = 5, Title = "Three", Url = "https://three", Tags = ["Work", "work", "workshop"] },
            new() { Id = 6, Title = "Four", Url = "https://four" },
        ]);
    }

    public async Task DisposeAsync() { await store.DisposeAsync(); File.Delete(path); }

    async Task<string?[]> TagsOf(int nodeId) =>
        (await store.GetByNodeIdsAsync([nodeId])).Single().Tags?.Split('\u001F') ?? [];

    [Fact]
    public async Task ListGroupsCaseInsensitivelyAndNamesTheMostUsedSpelling()
    {
        var summary = await store.GetTagsAsync();
        Assert.Equal(["dev", "News", "Work", "workshop"], summary.Tags.Select(t => t.Name));
        // "Work" x2 beats "work" x2 only on the ordinal tie-break; node 5 counts once.
        Assert.Equal(3, summary.Tags.Single(t => t.Name == "Work").Count);
        Assert.Equal(1, summary.Untagged);
    }

    [Fact]
    public async Task FilterMatchesTheWholeTagOnly()
    {
        Assert.Equal([2, 3, 5], (await store.GetBookmarksByTagAsync("WORK")).Select(r => r.NodeId));
        Assert.Equal([5], (await store.GetBookmarksByTagAsync("workshop")).Select(r => r.NodeId));
        Assert.Empty(await store.GetBookmarksByTagAsync("wor"));
        // Untagged: bookmarks only, never the folder or the separator.
        Assert.Equal([6], (await store.GetBookmarksByTagAsync(null)).Select(r => r.NodeId));
    }

    [Fact]
    public async Task RenameUnifiesSpellingsKeepsOrderAndMarksDirty()
    {
        var version = store.ContentVersion;
        Assert.Equal(3, await store.RenameTagAsync("work", " Job "));
        Assert.Equal(["Job", "News"], await TagsOf(2));
        Assert.Equal(["Job", "dev"], await TagsOf(3));
        Assert.Equal(["Job", "workshop"], await TagsOf(5));
        Assert.Equal(version + 1, store.ContentVersion);
        Assert.Equal("1", await store.GetMetaAsync(BookmarkStore.MetaDirty));
        // The search index follows the column.
        Assert.Equal(3, (await store.SearchAsync("Job")).Count);
        Assert.Equal(["dev", "Job", "News", "workshop"], (await store.GetTagsAsync()).Tags.Select(t => t.Name));
    }

    [Fact]
    public async Task RenameOntoAnExistingTagMerges()
    {
        Assert.Equal(1, await store.RenameTagAsync("dev", "WORK"));
        Assert.Equal(["WORK"], await TagsOf(3));
        // Bookmarks without the renamed tag keep their own spelling.
        Assert.Equal(["work", "News"], await TagsOf(2));
        var work = (await store.GetTagsAsync()).Tags.Single(t => string.Equals(t.Name, "work", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, work.Count);
    }

    [Fact]
    public async Task DeleteRemovesTheTagAndClearsEmptyLists()
    {
        Assert.Equal(1, await store.RenameTagAsync("NEWS", null));
        Assert.Equal(["work"], await TagsOf(2));
        Assert.Equal(1, await store.RenameTagAsync("workshop", null));
        Assert.Equal(3, await store.RenameTagAsync("work", null));
        Assert.Empty(await TagsOf(5));
        Assert.Equal(["dev"], (await store.GetTagsAsync()).Tags.Select(t => t.Name));
        Assert.Equal(3, (await store.GetTagsAsync()).Untagged);
    }

    [Fact]
    public async Task UnknownTagOrInvalidNameChangesNothing()
    {
        var version = store.ContentVersion;
        Assert.Equal(0, await store.RenameTagAsync("missing", "x"));
        Assert.Equal(0, await store.RenameTagAsync("dev", "dev"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RenameTagAsync("dev", "a,b"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RenameTagAsync("dev", "  "));
        Assert.Equal(version, store.ContentVersion);
        Assert.False(BookmarkStore.IsValidTagName("a;b"));
        Assert.True(BookmarkStore.IsValidTagName("c++ tips"));
    }
}
