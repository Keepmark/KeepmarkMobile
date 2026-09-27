using Keepmark.Core.Models;
using Keepmark.Core.Sync;

namespace Keepmark.Core.Tests;

public sealed class BookmarkMergeTests
{
    private static List<BookmarkNode> Remote() =>
    [
        new() { Title = "Other", Children =
        [
            new() { Id = 1, Title = "Shared", Url = "https://shared.test", Description = "remote meta" },
            new() { Id = 2, Title = "Remote only", Url = "https://remote.test" },
            new() { Id = 3, Title = "Docs", Children =
            [
                new() { Id = 4, Title = "Manual", Url = "https://manual.test" },
            ] },
        ] },
        new() { Title = "Menu", Children = [] },
    ];

    private static List<BookmarkNode> Local() =>
    [
        new() { Title = "Other", Children =
        [
            new() { Title = "Shared renamed locally", Url = "https://shared.test" },
            new() { Title = "Local only", Url = "https://local.test" },
            new() { Title = "Docs", Children =
            [
                new() { Title = "Guide", Url = "https://guide.test" },
            ] },
            new() { Title = "New folder", Children =
            [
                new() { Title = "Nested", Url = "https://nested.test" },
            ] },
            new() { Title = "───", Url = "xbs:separator" },
        ] },
    ];

    private static List<string> Urls(List<BookmarkNode> tree)
    {
        var out_ = new List<string>();
        var stack = new Stack<BookmarkNode>(tree);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Url is not null && !node.IsSeparator)
            {
                out_.Add(node.Url);
            }

            if (node.Children is not null)
            {
                foreach (var child in node.Children)
                {
                    stack.Push(child);
                }
            }
        }

        return out_;
    }

    private static List<int> Ids(List<BookmarkNode> tree)
    {
        var out_ = new List<int>();
        var stack = new Stack<BookmarkNode>(tree);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Id is { } id)
            {
                out_.Add(id);
            }

            if (node.Children is not null)
            {
                foreach (var child in node.Children)
                {
                    stack.Push(child);
                }
            }
        }

        return out_;
    }

    private static BookmarkNode? FindByUrl(List<BookmarkNode> tree, string url)
    {
        var stack = new Stack<BookmarkNode>(tree);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Url == url)
            {
                return node;
            }

            if (node.Children is not null)
            {
                foreach (var child in node.Children)
                {
                    stack.Push(child);
                }
            }
        }

        return null;
    }

    [Fact]
    public void Merge_UnionsByUrlKeepingRemoteNodesIdsAndMetadata()
    {
        var local = Local();
        var remote = Remote();
        var localBefore = System.Text.Json.JsonSerializer.Serialize(local);
        var remoteBefore = System.Text.Json.JsonSerializer.Serialize(remote);

        var result = BookmarkMerge.Merge(local, remote);

        Assert.Equal(localBefore, System.Text.Json.JsonSerializer.Serialize(local)); // inputs untouched
        Assert.Equal(remoteBefore, System.Text.Json.JsonSerializer.Serialize(remote));
        Assert.Equal(
            new[] {"https://shared.test", "https://remote.test", "https://manual.test",
             "https://local.test", "https://guide.test", "https://nested.test"}.OrderBy(u => u).ToList(),
            Urls(result.Tree).OrderBy(u => u).ToList());
        var kept = FindByUrl(result.Tree, "https://shared.test");
        Assert.NotNull(kept);
        Assert.Equal(1, kept.Id);
        Assert.Equal("Shared", kept.Title);
        Assert.Equal("remote meta", kept.Description);
        Assert.Equal(3, result.Stats.LocalOnly);
        Assert.Equal(2, result.Stats.RemoteOnly);
        Assert.Equal(1, result.Stats.TitleConflicts);
        Assert.Equal(1, result.Stats.FoldersCreated);
        Assert.Equal(1, result.Stats.SeparatorsAdopted);
    }

    [Fact]
    public void Merge_ProducesUniqueIdsAboveRemoteMax()
    {
        var result = BookmarkMerge.Merge(Local(), Remote());
        var all = Ids(result.Tree);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(all.Where(id => id > 4), id => Assert.True(id > 4));
        Assert.Contains(all, id => id > 4);
    }

    [Fact]
    public void Merge_MergesIntoMatchedFoldersKeepingRemoteOrderFirst()
    {
        var result = BookmarkMerge.Merge(Local(), Remote());
        var other = result.Tree.First(c => c.Title == "Other");
        var docs = other.Children!.First(b => b.Title == "Docs");
        Assert.Equal(3, docs.Id);
        Assert.Equal(["https://manual.test", "https://guide.test"],
            docs.Children!.Select(b => b.Url).ToList());
    }

    [Fact]
    public void Merge_HandlesEmptySides()
    {
        var onlyRemote = BookmarkMerge.Merge([], Remote());
        Assert.Equal(Urls(Remote()).OrderBy(u => u).ToList(), Urls(onlyRemote.Tree).OrderBy(u => u).ToList());
        Assert.Equal(0, onlyRemote.Stats.LocalOnly);

        var onlyLocal = BookmarkMerge.Merge(Local(), []);
        Assert.Equal(Urls(Local()).OrderBy(u => u).ToList(), Urls(onlyLocal.Tree).OrderBy(u => u).ToList());
        Assert.Equal(0, onlyLocal.Stats.RemoteOnly);
        Assert.Contains(onlyLocal.Tree, c => c.Title == "Other");

        Assert.Empty(BookmarkMerge.Merge([], []).Tree);
    }

    [Fact]
    public void Merge_TreatsTrackingParamVariantsAsSameBookmark()
    {
        var local = new List<BookmarkNode>
        {
            new() { Title = "Other", Children =
            [
                new() { Title = "A", Url = "https://a.test/?utm_source=x" },
            ] },
        };
        var remote = new List<BookmarkNode>
        {
            new() { Title = "Other", Children =
            [
                new() { Id = 1, Title = "A", Url = "https://a.test/" },
            ] },
        };
        var result = BookmarkMerge.Merge(local, remote);
        Assert.Equal(new[] {"https://a.test/"}.ToList(), Urls(result.Tree).ToList());
        Assert.Equal(0, result.Stats.LocalOnly);
        Assert.Equal(0, result.Stats.TitleConflicts);
    }

    [Fact]
    public void Merge_FlattensCorruptBookmarkWithChildrenInsteadOfDropping()
    {
        var local = new List<BookmarkNode>
        {
            new() { Title = "Other", Children =
            [
                new() { Title = "Weird", Url = "https://weird.test", Children =
                [
                    new() { Title = "Kid", Url = "https://kid.test" },
                ] },
            ] },
        };
        var result = BookmarkMerge.Merge(local, []);
        Assert.Equal(new[] {"https://kid.test", "https://weird.test"}.OrderBy(u => u).ToList(),
            Urls(result.Tree).OrderBy(u => u).ToList());
    }

    [Theory]
    [InlineData("https://h.test/?fbclid=abc", "https://h.test/")]
    [InlineData("https://h.test/page/?utm_medium=x&keep=1", "https://h.test/page?keep=1")]
    [InlineData("HTTPS://H.TEST/Page", "https://h.test/Page")]
    public void NormalizeUrl_StripsTrackingAndNormalizesHost(string input, string expected)
    {
        Assert.Equal(expected, BookmarkMerge.NormalizeUrl(input));
    }

    [Theory]
    [InlineData("new", "push", "https://n.test/", null,
        "Created a new sync on https://n.test/ and uploaded your bookmarks.")]
    [InlineData("existing", "push", "https://n.test/", "s1",
        "Overwrote the remote sync s1 on https://n.test/ with your local bookmarks.")]
    [InlineData("existing", "merge", "https://n.test/", "s1",
        "Merged your local bookmarks with the remote sync s1 on https://n.test/ (nothing deleted).")]
    [InlineData("existing", "pull", "https://n.test/", "s1",
        "Replaced your local bookmarks with the remote sync s1 from https://n.test/.")]
    public void MoveResult_NamesModeDirectionAndTarget(
        string mode, string direction, string url, string? syncId, string expected)
    {
        Assert.Equal(expected, MoveResult.Describe(mode, direction, url, syncId));
    }
}
