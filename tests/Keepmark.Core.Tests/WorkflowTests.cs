using Keepmark.Core.Workflow;
using Xunit;

namespace Keepmark.Core.Tests;

public class WorkflowTests
{
    [Theory]
    [InlineData("short", "short", "Use a password of at least 8 characters.")]
    [InlineData("long-enough", "long-enouGh", "Passwords do not match.")]
    [InlineData("long-enough", "long-enough", null)]
    public void NewPasswordRule(string password, string confirm, string? expected) =>
        Assert.Equal(expected, WorkflowRules.CheckNewPassword(password, confirm));

    [Fact]
    public void LengthIsCheckedBeforeMatch() =>
        Assert.Equal(WorkflowRules.PasswordTooShort, WorkflowRules.CheckNewPassword("abc", "xyz"));

    [Fact]
    public void RequireNewPasswordThrowsTheShownText()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowRules.RequireNewPassword("abc", "abc"));
        Assert.Equal(WorkflowRules.PasswordTooShort, ex.Message);
    }

    [Theory]
    [InlineData(null, "…")]
    [InlineData("12345678", "…")]
    [InlineData("abcdef0123456789", "abcd…89")]
    public void ShortIdNeverShowsTheWholeBearer(string? id, string expected) =>
        Assert.Equal(expected, WorkflowRules.ShortId(id));

    [Theory]
    [InlineData("Https://api.xbrowsersync.org", "https://api.xbrowsersync.org")]
    [InlineData("  HTTPS://Sync.Example.ORG:8443/Api/ ", "https://sync.example.org:8443/Api/")]
    [InlineData("https://api.xbrowsersync.org", "https://api.xbrowsersync.org")]
    [InlineData("not a url", "not a url")]
    [InlineData("ftp://Host", "ftp://Host")]
    [InlineData(null, "")]
    public void NormalizeServiceUrl(string? raw, string expected) =>
        Assert.Equal(expected, WorkflowRules.NormalizeServiceUrl(raw));

    [Theory]
    [InlineData("https://sync.example.org:8443/Api/x", "https", "sync.example.org:8443/Api/x")]
    [InlineData("HTTP://Sync.Example.ORG/Api", "http", "sync.example.org/Api")]
    [InlineData("https://xbs.eris.cc", "https", "xbs.eris.cc")]
    // Not an absolute http(s) URL: shown whole, under the safe default scheme.
    [InlineData("sync.example.org", "https", "sync.example.org")]
    [InlineData("ftp://host/x", "https", "ftp://host/x")]
    [InlineData("  api.xbrowsersync.org  ", "https", "api.xbrowsersync.org")]
    [InlineData("", "https", "")]
    [InlineData(null, "https", "")]
    public void SplitServiceUrl(string? raw, string scheme, string host) =>
        Assert.Equal((scheme, host), WorkflowRules.SplitServiceUrl(raw));

    [Theory]
    [InlineData("https", "sync.example.org", "https://sync.example.org")]
    [InlineData("https", "sync.example.org:8443/Api", "https://sync.example.org:8443/Api")]
    [InlineData("http", "sync.example.org", "http://sync.example.org")]
    // Android keyboards capitalize the first letter of the host box.
    [InlineData("https", "Api.xbrowsersync.org", "https://Api.xbrowsersync.org")]
    // A pasted URL wins over the picker: the user meant that scheme.
    [InlineData("http", "https://xbs.eris.cc", "https://xbs.eris.cc")]
    [InlineData("https", "http://localhost:8080", "http://localhost:8080")]
    // Empty host never yields a dangling listener-less scheme.
    [InlineData("https", "", "")]
    [InlineData("https", null, "")]
    [InlineData("https", "  ", "")]
    [InlineData(null, "sync.example.org", "https://sync.example.org")]
    [InlineData("gopher", "sync.example.org", "https://sync.example.org")]
    public void ComposeServiceUrl(string? scheme, string? host, string expected) =>
        Assert.Equal(expected, WorkflowRules.ComposeServiceUrl(scheme, host));

    [Theory]
    // Round-trips what the app stores: the authority is already lower case
    // (NormalizeServiceUrl lowercases it), so split+compose is idempotent on
    // any stored URL — including ones carrying a path or a port.
    [InlineData("https://sync.example.org:8443/Api")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://api.xbrowsersync.org")]
    public void SplitAndComposeAreInverses(string url)
    {
        var (scheme, host) = WorkflowRules.SplitServiceUrl(url);
        Assert.Equal(url, WorkflowRules.ComposeServiceUrl(scheme, host));
    }

    [Theory]
    [InlineData("https://sync.example.org", false)]
    [InlineData("https://sync.example.org:8443/api", false)]
    [InlineData("http://192.168.1.50:8090", true)]
    [InlineData("HTTP://Localhost:8090", true)]
    [InlineData("http://sync.example.org", true)]
    [InlineData("sync.example.org", false)]      // no scheme yet: nothing is sent
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCleartextServiceUrlOnlyForHttp(string? url, bool expected) =>
        Assert.Equal(expected, WorkflowRules.IsCleartextServiceUrl(url));

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0, "kept forever")]
    [InlineData(1, "deleted after 1 day without use")]
    [InlineData(21, "deleted after 21 days without use")]
    public void DescribeSyncExpiry(int? days, string expected) =>
        Assert.Equal(expected, WorkflowRules.DescribeSyncExpiry(days));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(3 * 1024 * 1024, "3 MB")]
    public void FormatBytes(long bytes, string expected) =>
        Assert.Equal(expected, WorkflowRules.FormatBytes(bytes));

    [Fact]
    public void ImportTextsSayWhoIsAffected()
    {
        Assert.Contains("other devices", WorkflowTexts.ImportImplication(linked: true));
        Assert.DoesNotContain("server", WorkflowTexts.ImportImplication(linked: false));
        Assert.Contains("Safety backups", WorkflowTexts.ImportDone(3, linked: false));
    }

    [Fact]
    public void SafetyRestoreTextNamesTheReplacedSide()
    {
        Assert.StartsWith("This device's bookmarks", WorkflowTexts.SafetyRestoreImplication("local", 4));
        Assert.StartsWith("The server copy", WorkflowTexts.SafetyRestoreImplication("remote", 4));
    }

    [Fact]
    public void UnlinkTextSaysWhatIsNeededToLinkAgain()
    {
        Assert.Contains("sync ID and password", WorkflowTexts.UnlinkImplication);
        Assert.Contains("Unsynced edits stay only here", WorkflowTexts.UnlinkImplication);
    }

    [Fact]
    public void DirectionsComeInOneOrderWithOneLabelEach()
    {
        Assert.Equal(["download", "merge", "upload"], WorkflowTexts.DirectionOrder);
        Assert.Equal(["Yes, download", "Yes, merge", "Yes, upload"],
            WorkflowTexts.DirectionOrder.Select(d => WorkflowTexts.DirectionConfirm(d).ConfirmLabel));
        Assert.Contains("bookmarks will be replaced", WorkflowTexts.DirectionConfirm("download").Implication);
        Assert.Contains("Other linked devices", WorkflowTexts.DirectionConfirm("upload").Implication);
        Assert.Contains("only one copy may return", WorkflowTexts.DirectionConfirm("merge").Implication);
        foreach (var direction in WorkflowTexts.DirectionOrder)
            Assert.Contains("Backup → Safety backups", WorkflowTexts.DirectionConfirm(direction).Implication);
    }

    [Fact]
    public void CreateTextsSayWhatToKeep()
    {
        Assert.Contains("sync ID", WorkflowTexts.CreatedNext);
        Assert.Contains("password", WorkflowTexts.CreatedNext);
        Assert.Contains("Safety backups", WorkflowTexts.CreateEmptyImplication);
    }

    [Fact]
    public void DuplicateTextsMatchTheBrowser()
    {
        var c = WorkflowTexts.DupDeleteConfirm(3);
        Assert.Equal("Delete 3 marked duplicates?", c.Message);
        Assert.Contains("One copy of each URL always stays", c.Implication);
        Assert.Equal("Yes, delete", c.ConfirmLabel);
        Assert.Contains("Backup → Safety backups", WorkflowTexts.DupDeleteDone(3));
    }
}
