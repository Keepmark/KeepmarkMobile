using Keepmark.Core.Crypto;
using Keepmark.Core.Models;
using Xunit;

namespace Keepmark.Core.Tests;

public sealed class BackupGuardTests
{
    [Fact]
    public void RejectsEmptyAndShortPasswords()
    {
        Assert.Throws<InvalidOperationException>(() => BackupGuard.RequirePassword(null));
        Assert.Throws<InvalidOperationException>(() => BackupGuard.RequirePassword(string.Empty));
        Assert.Throws<InvalidOperationException>(() => BackupGuard.RequirePassword("short7!"));
        BackupGuard.RequirePassword("long-enough-1");
    }

    [Fact]
    public void RejectsMissingPath()
    {
        Assert.Throws<InvalidOperationException>(() => BackupGuard.RequirePath(null));
        Assert.Throws<InvalidOperationException>(() => BackupGuard.RequirePath("  "));
        BackupGuard.RequirePath("/tmp/x.xbs.json");
    }

    [Theory]
    [InlineData("x.xbs.json", true)]
    [InlineData("x.JSON", true)]
    [InlineData("x.txt", false)]
    [InlineData("xjson", false)]
    [InlineData("", false)]
    public void ExtensionRule(string name, bool expected)
    {
        Assert.Equal(expected, BackupGuard.HasSupportedExtension(name));
    }

    [Fact]
    public void SlugsTheSyncNameForTheFileName()
    {
        Assert.Equal("lavoro", BackupGuard.Slug("Lavoro"));
        Assert.Equal("casa_e_ufficio", BackupGuard.Slug("Casa e ufficio"));
        Assert.Equal("pero_citta", BackupGuard.Slug("  Però — Città  "));
        Assert.Equal("api_xbrowsersync_org", BackupGuard.Slug("api.xbrowsersync.org"));
        // Nothing usable left: no stray separators in the file name.
        Assert.Equal(string.Empty, BackupGuard.Slug("···"));
        Assert.Equal(string.Empty, BackupGuard.Slug(null));
        Assert.True(BackupGuard.Slug(new string('x', 80)).Length <= BackupGuard.MaxSlugLength);
    }

    [Fact]
    public void ExportFileNameCarriesTheSyncName()
    {
        var when = new DateTime(2026, 11, 6, 9, 15, 0, DateTimeKind.Utc);
        Assert.Equal("xbs-backup-casa_e_ufficio-20261106-091500.xbs.json",
            BackupGuard.ExportFileName("Casa e ufficio", when));
        Assert.Equal("xbs-backup-20261106-091500.xbs.json", BackupGuard.ExportFileName(null, when));
        Assert.Equal("xbs-backup-20261106-091500.xbs.json", BackupGuard.ExportFileName("···", when));
    }

    [Fact]
    public void CountsTreeAndRejectsNullNodes()
    {
        var roots = new List<BookmarkNode>
        {
            new() { Title = "a", Children = [new() { Title = "b" }, new() { Title = "c" }] },
            new() { Title = "d", Url = "https://e" },
        };
        Assert.Equal(4, BackupGuard.ValidateTree(roots));

        var bad = new List<BookmarkNode> { new() { Title = "a", Children = [null!] } };
        Assert.Throws<InvalidOperationException>(() => BackupGuard.ValidateTree(bad));
    }
}
