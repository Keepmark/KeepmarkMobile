using Keepmark.Core.Safety;
using Xunit;

namespace Keepmark.Core.Tests;

public sealed class SafetyBackupTests
{
    [Fact]
    public void FileName_IsReadableAndTimestamped()
    {
        var name = SafetyBackupFiles.BuildFileName(
            new DateTime(2026, 9, 26, 14, 30, 0, DateTimeKind.Utc), SafetyOperations.TakeRemote);
        Assert.Equal("safety-20260926-143000-take-remote.json", name);
    }

    [Fact]
    public void Slug_Sanitizes()
    {
        Assert.Equal("take-remote", SafetyBackupFiles.Slug("Take Remote!"));
        Assert.Equal("backup", SafetyBackupFiles.Slug("!!!"));
    }

    [Theory]
    [InlineData(SafetyOperations.TakeRemote, "Connect")]
    [InlineData(SafetyOperations.UploadLocal, "Connect")]
    [InlineData(SafetyOperations.ChangePassword, "Settings")]
    [InlineData(SafetyOperations.DuplicatesCleanup, "duplicates")]
    [InlineData(SafetyOperations.SwitchMerge, "Switch service")]
    [InlineData(SafetyOperations.ConnectMerge, "Connect")]
    [InlineData(SafetyOperations.PreRestore, "restore")]
    [InlineData("whatever", "Older")]
    public void Describe_NamesOperation(string op, string fragment)
    {
        Assert.Contains(fragment, SafetyOperations.Describe(op));
    }

    [Fact]
    public void Prune_KeepsNewestFive()
    {
        var entries = Enumerable.Range(0, 8).Select(i => new SafetyBackupEntry(
            $"safety-{i}.json", new DateTime(2026, 1, i + 1, 0, 0, 0, DateTimeKind.Utc),
            SafetyOperations.TakeRemote, "local", i, i, null)).ToList();
        var drop = SafetyBackupFiles.Prune(entries);
        Assert.Equal(3, drop.Count);
        Assert.DoesNotContain("safety-7.json", drop);
        Assert.Contains("safety-0.json", drop);
    }

    [Fact]
    public void ParseLegacy_ReadsOldShape()
    {
        var e = SafetyBackupFiles.ParseLegacy("safety-local-20260925-213022.json", 123);
        Assert.NotNull(e);
        Assert.Equal("local", e.Side);
        Assert.Equal(SafetyOperations.Legacy, e.Operation);
        Assert.Equal(2026, e.CreatedAtUtc.Year);
        Assert.Equal(123, e.SizeBytes);
        Assert.Null(SafetyBackupFiles.ParseLegacy("safety-nope.json", 0));
        Assert.Null(SafetyBackupFiles.ParseLegacy("other.json", 0));
        Assert.Null(SafetyBackupFiles.ParseLegacy("safety-sideways-2026.json", 0));
    }

    [Fact]
    public void Index_RoundTrips_AndSurvivesDamage()
    {
        var entries = new List<SafetyBackupEntry>
        {
            new("safety-a.json", DateTime.UtcNow, SafetyOperations.UploadLocal, "remote", 42, 99, "ab12…ef"),
        };
        var back = SafetyBackupFiles.ParseIndex(SafetyBackupFiles.SerializeIndex(entries));
        Assert.Single(back);
        Assert.Equal(42, back[0].Items);
        Assert.Empty(SafetyBackupFiles.ParseIndex("{damaged"));
        Assert.Empty(SafetyBackupFiles.ParseIndex(""));
    }

    [Fact]
    public void Fingerprint_IsStableShortHex()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var fp = SafetyMaster.Fingerprint(key);
        Assert.Equal(8, fp.Length);
        Assert.Equal(fp, SafetyMaster.Fingerprint(key));
        Assert.Matches("^[0-9a-f]{8}$", fp);
    }

    [Fact]
    public void Wraps_AddRefreshTrimAndFind()
    {
        var wraps = SafetyMaster.AddWrap([], "aaa", "w1");
        wraps = SafetyMaster.AddWrap(wraps, "bbb", "w2");
        wraps = SafetyMaster.AddWrap(wraps, "aaa", "w1b"); // refresh moves first
        Assert.Equal(new[] { "aaa", "bbb" }, wraps.Select(w => w.KekId));
        Assert.Equal("w1b", SafetyMaster.FindWrap(wraps, "aaa")!.WrappedKey);
        Assert.Null(SafetyMaster.FindWrap(wraps, "zzz"));
        wraps = SafetyMaster.AddWrap(wraps, "ccc", "w3");
        wraps = SafetyMaster.AddWrap(wraps, "ddd", "w4");
        Assert.Equal(SafetyMaster.MaxWraps, wraps.Count);
        Assert.DoesNotContain(wraps, w => w.KekId == "bbb"); // oldest trimmed
    }
}
