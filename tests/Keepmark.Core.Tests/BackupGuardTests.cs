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
