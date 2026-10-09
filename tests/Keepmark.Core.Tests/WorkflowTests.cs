using Keepmark.Core.Workflow;
using Xunit;

namespace Keepmark.Core.Tests;

public class WorkflowTests
{
    [Theory]
    [InlineData("short", "short", WorkflowRules.PasswordTooShort)]
    [InlineData("long-enough", "long-enouGh", WorkflowRules.PasswordsDoNotMatch)]
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
        Assert.Contains("sync ID and its password", WorkflowTexts.UnlinkImplication);
        Assert.Contains("not yet synced", WorkflowTexts.UnlinkImplication);
    }

    [Fact]
    public void DirectionsComeInOneOrderWithOneLabelEach()
    {
        Assert.Equal(["download", "merge", "upload"], WorkflowTexts.DirectionOrder);
        Assert.Equal(["Yes, download", "Yes, merge", "Yes, upload"],
            WorkflowTexts.DirectionOrder.Select(d => WorkflowTexts.DirectionConfirm(d).ConfirmLabel));
        Assert.Contains("not yet synced are lost", WorkflowTexts.DirectionConfirm("download").Implication);
        Assert.Contains("other devices", WorkflowTexts.DirectionConfirm("upload").Implication);
    }

    [Fact]
    public void MoveTextsSayWhatStaysBehind()
    {
        Assert.Contains("old sync stays on the old service", WorkflowTexts.MoveImplication);
        Assert.Contains("other devices stay linked", WorkflowTexts.MoveImplication);
        Assert.Equal("Create a new sync on example.org and upload 12 bookmarks?", WorkflowTexts.MoveNewConfirm("example.org", 12));
    }
}
