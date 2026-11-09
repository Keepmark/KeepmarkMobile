using System.Security.Cryptography;
using System.Text;
using Keepmark.Core.Crypto;
using Keepmark.Core.Data;
using Keepmark.Core.Html;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;

namespace Keepmark.Core.Tests;

public class NetscapeBookmarksTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "html", name), Encoding.UTF8);

    private static IEnumerable<BookmarkNode> Walk(IEnumerable<BookmarkNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.Children is not null)
            {
                foreach (var child in Walk(node.Children)) yield return child;
            }
        }
    }

    [Fact]
    public void ChromeExportKeepsStructureDecodesEntitiesAndSkipsBookmarklets()
    {
        var r = NetscapeBookmarks.Parse(Fixture("chrome.html"));

        Assert.Equal(2, r.Roots.Count);
        Assert.Equal("Bookmarks bar", r.Roots[0].Title);
        Assert.Equal(1_700_000_000_000, r.Roots[0].DateAdded);
        var bar = r.Roots[0].Children!;
        Assert.Equal("GitHub", bar[0].Title);
        Assert.Equal("https://github.com/", bar[0].Url);
        Assert.Equal(1_700_000_100_000, bar[0].DateAdded);
        Assert.Equal("Dev & Ops", bar[1].Title);
        var dev = Assert.Single(bar[1].Children!);
        Assert.Equal("Docs <v2>", dev.Title);
        Assert.Equal("https://docs.example.com/a?b=1&c=2", dev.Url);
        Assert.Equal("Caffè ☕ news", r.Roots[1].Children![0].Title);
        Assert.Equal((3, 3, 0, 1), (r.Folders, r.Bookmarks, r.Separators, r.Skipped));
        Assert.All(Walk(r.Roots), n => Assert.Null(n.Id));
    }

    [Fact]
    public void FirefoxExportReadsTagsDescriptionsSeparatorsAndIgnoresPlaceQueries()
    {
        var r = NetscapeBookmarks.Parse(Fixture("firefox.html"));

        var firefox = r.Roots[0];
        Assert.Equal("Firefox", firefox.Title);
        Assert.Equal(["browser", "mozilla"], firefox.Tags);
        Assert.Equal("The browser's home page", firefox.Description);
        Assert.True(r.Roots[1].IsSeparator);
        var reading = r.Roots[2];
        Assert.Equal("Reading", reading.Title);
        Assert.Null(reading.Description);
        var article = Assert.Single(reading.Children!);
        Assert.Equal("An article", article.Title);
        Assert.Equal(["later", "long read"], article.Tags);
        Assert.Equal("Bookmarks Toolbar", r.Roots[3].Title);
        Assert.Empty(r.Roots[4].Children!);
        Assert.Equal((3, 3, 1, 1), (r.Folders, r.Bookmarks, r.Separators, r.Skipped));
    }

    [Fact]
    public void SafariAndPocketExportsAreRead()
    {
        var safari = NetscapeBookmarks.Parse(Fixture("safari.html"));
        Assert.Equal(["Favourites", "Reading List"], safari.Roots.Select(x => x.Title));
        var untitled = safari.Roots[0].Children![1];
        Assert.Equal("https://example.com/single?q=1", untitled.Title);
        Assert.Null(untitled.DateAdded);
        Assert.Equal("http://example.com/read", safari.Roots[1].Children![0].Url);

        var pocket = NetscapeBookmarks.Parse(Fixture("pocket.html"));
        Assert.Equal(2, pocket.Bookmarks);
        Assert.Equal(["tech", "ai"], pocket.Roots[0].Tags);
        Assert.Null(pocket.Roots[1].Tags);
    }

    [Fact]
    public void RoundTripKeepsContainersTagsDescriptionsDatesAndSeparators()
    {
        List<BookmarkNode> tree =
        [
            new() { Id = 1, Title = ContainerNames.Menu, Children = [
                new() { Id = 2, Title = "A \"quoted\" <b>", Url = "https://a.example/?x=1&y=2", DateAdded = 1_700_000_123_456,
                    Tags = ["one", "two,three"], Description = "line one\nline two" },
                new() { Id = 3, Url = XbsConstants.SeparatorUrl, Title = "---" },
            ]},
            new() { Id = 4, Title = ContainerNames.Toolbar, Children = [
                new() { Id = 5, Title = "Nested", DateAdded = 1_600_000_000_000, Children = [
                    new() { Id = 6, Title = "Deep", Url = "https://deep.example/" },
                ]},
            ]},
            new() { Id = 7, Title = ContainerNames.Other, Children = [] },
        ];

        var html = NetscapeBookmarks.Write(tree);
        Assert.Contains("<!DOCTYPE NETSCAPE-Bookmark-file-1>", html);
        Assert.Contains("PERSONAL_TOOLBAR_FOLDER=\"true\"", html);
        Assert.Contains("UNFILED_BOOKMARKS_FOLDER=\"true\"", html);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("[xbs]", html);

        var back = NetscapeBookmarks.Parse(html);
        var a = back.Roots[0];
        Assert.Equal("A \"quoted\" <b>", a.Title);
        Assert.Equal("https://a.example/?x=1&y=2", a.Url);
        Assert.Equal(1_700_000_123_000, a.DateAdded); // seconds precision
        Assert.Equal(["one", "two three"], a.Tags);
        Assert.Equal("line one line two", a.Description);
        Assert.True(back.Roots[1].IsSeparator);
        Assert.Equal("Toolbar", back.Roots[2].Title);
        Assert.Equal("Deep", back.Roots[2].Children![0].Children![0].Title);
        Assert.Equal(1_600_000_000_000, back.Roots[2].Children![0].DateAdded);
        Assert.Empty(back.Roots[3].Children!);
        Assert.Equal((3, 2, 1, 0), (back.Folders, back.Bookmarks, back.Separators, back.Skipped));
    }

    [Theory]
    [InlineData("1700000000", 1_700_000_000_000L)]
    [InlineData("1700000000123", 1_700_000_000_123L)]
    [InlineData("1700000000123456", 1_700_000_000_123L)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void DatesAcceptSecondsMillisecondsAndMicroseconds(string? raw, long? expected) =>
        Assert.Equal(expected, NetscapeBookmarks.ParseDate(raw));

    [Fact]
    public void NonBookmarkTextIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NetscapeBookmarks.Parse("{\"not\":\"html\"}"));
        Assert.Contains("not a bookmarks file", ex.Message);
        Assert.Empty(NetscapeBookmarks.Parse("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<DL><p></DL>").Roots);
    }

    [Fact]
    public void HostileInputIsBounded()
    {
        var deep = new StringBuilder("<!DOCTYPE NETSCAPE-Bookmark-file-1><DL>");
        for (int i = 0; i < NetscapeBookmarks.MaxDepth + 5; i++) deep.Append("<DT><H3>f</H3><DL>");
        Assert.Throws<InvalidOperationException>(() => NetscapeBookmarks.Parse(deep.ToString()));

        var many = new StringBuilder("<DL>");
        for (int i = 0; i <= BackupGuard.MaxNodes; i++) many.Append("<HR>");
        Assert.Throws<InvalidOperationException>(() => NetscapeBookmarks.Parse(many.ToString()));

        // Truncated / unterminated markup never throws past the guard.
        var cut = NetscapeBookmarks.Parse("<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><DT><A HREF=\"https://x.example/\">never closed");
        Assert.Equal("never closed", cut.Roots[0].Title);
        NetscapeBookmarks.Parse("<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><DT><A HREF=\"https://x.example/");
        NetscapeBookmarks.Parse("<!DOCTYPE NETSCAPE-Bookmark-file-1><!-- unterminated");
    }

    [Fact]
    public void DamagedMarkupLosesOneItemNotTheRestOfTheFile()
    {
        // An unclosed link stops at the next item instead of swallowing it.
        var unclosed = NetscapeBookmarks.Parse(
            "<DL><DT><A HREF=\"https://a.example/\">First <b>bold</b>\n" +
            "<DT><A HREF=\"https://b.example/\">Second</A>\n" +
            "<DT><H3>Folder\n<DL><DT><A HREF=\"https://c.example/\">Third</A></DL></DL>");
        Assert.Equal(["First bold", "Second", "Folder"], unclosed.Roots.Select(r => r.Title));
        Assert.Equal("Third", unclosed.Roots[2].Children!.Single().Title);

        // An unbalanced quote in one tag does not drop what follows.
        var quote = NetscapeBookmarks.Parse(
            "<DL><DT><A HREF=\"https://a.example/\">One</A>\n" +
            "<DT><A HREF=\"https://b.example/ ICON='x>Two</A>\n" +
            "<DT><A HREF=https://c.example/>Three</A></DL>");
        Assert.Contains(quote.Roots, r => r.Title == "One");
        Assert.Contains(quote.Roots, r => r.Title == "Three");

        // Many unclosed links stay linear (was quadratic: one scan to the end each).
        var many = new StringBuilder("<DL>");
        for (int i = 0; i < 50_000; i++) many.Append("<DT><A HREF=\"https://x.example/").Append(i).Append("\">t");
        Assert.Equal(50_000, NetscapeBookmarks.Parse(many.ToString()).Bookmarks);
    }

    [Fact]
    public void ExportSkipsNodesThatAreNeitherFolderNorLink()
    {
        var html = NetscapeBookmarks.Write([
            new() { Title = ContainerNames.Other, Children = [
                new() { Title = "No url" },
                new() { Title = "Link", Url = "https://a.example/" },
            ] },
        ]);
        Assert.DoesNotContain("No url", html);
        Assert.DoesNotContain("HREF=\"\"", html);
        Assert.Contains(">Link</A>", html);
    }

    [Fact]
    public void ControlCharactersNeverReachTitlesOrTags()
    {
        var r = NetscapeBookmarks.Parse("<DL><DT><A HREF=\"https://x.example/\" TAGS=\"a&#31;b,c\">t&#31;i\u0001tle</A></DL>");
        Assert.Equal("t i tle", r.Roots[0].Title);
        Assert.Equal(["a b", "c"], r.Roots[0].Tags);
    }

    [Fact]
    public void LongDescriptionsAreCut()
    {
        var r = NetscapeBookmarks.Parse($"<DL><DT><A HREF=\"https://x.example/\">t</A><DD>{new string('d', 500)}</DL>");
        Assert.Equal(XbsConstants.DescriptionMaxLength, r.Roots[0].Description!.Length);
    }

    [Fact]
    public void PlanCountsAndOptionallySkipsDuplicates()
    {
        var parsed = NetscapeBookmarks.Parse(
            "<DL><DT><H3>F</H3><DL><DT><A HREF=\"https://GitHub.com/?utm_source=x\">dup</A></DL>" +
            "<DT><A HREF=\"https://new.example/\">new</A><DT><A HREF=\"https://new.example/#top\">same in file</A><HR></DL>");
        List<BookmarkNode> existing =
        [
            new() { Id = 1, Title = ContainerNames.Other, Children = [new() { Id = 2, Title = "g", Url = "https://github.com/" }] },
        ];

        var keep = NetscapeBookmarks.Plan(parsed, existing, skipDuplicates: false);
        Assert.Equal((1, 3, 1, 2), (keep.Folders, keep.Bookmarks, keep.Separators, keep.Duplicates));

        var skip = NetscapeBookmarks.Plan(parsed, existing, skipDuplicates: true);
        Assert.Equal((0, 1, 1, 2), (skip.Folders, skip.Bookmarks, skip.Separators, skip.Duplicates));
        Assert.Equal("new", skip.Roots[0].Title);
        Assert.Equal(3, parsed.Bookmarks); // input untouched
    }

    [Fact]
    public void ExportNameIsSluggedLocalTime()
    {
        var at = new DateTime(2026, 11, 8, 14, 5, 9);
        Assert.Equal("keepmark-casa_mia-20261108-140509.html", NetscapeBookmarks.ExportFileName("Casa mia", at));
        Assert.Equal("keepmark-20261108-140509.html", NetscapeBookmarks.ExportFileName(null, at));
        Assert.True(NetscapeBookmarks.HasSupportedExtension("bookmarks.HTM"));
        Assert.False(NetscapeBookmarks.HasSupportedExtension("bookmarks.json"));
    }

    [Fact]
    public async Task ImportedFolderGoesUnderOtherWithFreshIdsAndMarksDirty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xbs-html-{Guid.NewGuid():N}.db");
        var store = new BookmarkStore(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), new InMemoryLogger());
        try
        {
            await store.InitializeAsync();
            await store.ReplaceAllAsync(
            [
                new() { Id = 1, Title = ContainerNames.Menu, Children = [] },
                new() { Id = 2, Title = ContainerNames.Other, Children = [new() { Id = 9, Title = "old", Url = "https://old.example/" }] },
            ], "blob", "2024-01-01T00:00:00.000Z", "1.8.0");

            var parsed = NetscapeBookmarks.Parse(Fixture("chrome.html"));
            var plan = NetscapeBookmarks.Plan(parsed, [], skipDuplicates: true);
            var inserted = await store.AddImportedFolderAsync(new BookmarkNode { Title = "Imported", Children = plan.Roots });

            var (roots, _, _, _) = await store.LoadMirrorAsync();
            var other = roots.Single(r => r.Title == ContainerNames.Other);
            Assert.Equal(["old", "Imported"], other.Children!.Select(c => c.Title));
            var imported = other.Children![1];
            var all = Walk([imported]).ToList();
            Assert.Equal(inserted, all.Count);
            Assert.All(all, n => Assert.True(n.Id > 9));
            Assert.Equal(all.Count, all.Select(n => n.Id).Distinct().Count());
            Assert.All(all, n => Assert.NotNull(n.DateAdded));
            Assert.Equal("1", await store.GetMetaAsync(BookmarkStore.MetaDirty));
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportCreatesTheOtherContainerWhenMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xbs-html-{Guid.NewGuid():N}.db");
        var store = new BookmarkStore(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), new InMemoryLogger());
        try
        {
            await store.InitializeAsync();
            await store.AddImportedFolderAsync(new BookmarkNode
            {
                Title = "Imported",
                Children = [new() { Title = "x", Url = "https://x.example/" }],
            });

            var (roots, _, _, _) = await store.LoadMirrorAsync();
            var other = Assert.Single(roots);
            Assert.Equal(ContainerNames.Other, other.Title);
            Assert.Equal("x", other.Children![0].Children![0].Title);
        }
        finally
        {
            await store.DisposeAsync();
            File.Delete(path);
        }
    }
}
