using Keepmark.Core.Models;
using Keepmark.Core.Safety;
using Keepmark.Core.Sync;
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
    [InlineData(SafetyOperations.ChangePassword, "Syncs")]
    [InlineData(SafetyOperations.DuplicatesCleanup, "duplicates")]
    [InlineData(SafetyOperations.SwitchMerge, "Sync switch")]
    [InlineData(SafetyOperations.ConnectMerge, "Connect")]
    [InlineData(SafetyOperations.PreRestore, "restore")]
    [InlineData("whatever", "Older")]
    public void Describe_NamesOperation(string op, string fragment)
    {
        Assert.Contains(fragment, SafetyOperations.Describe(op));
    }

    [Fact]
    public void Prune_KeepsNewestTen()
    {
        var entries = Enumerable.Range(0, 13).Select(i => new SafetyBackupEntry(
            $"safety-{i}.json", new DateTime(2026, 1, i + 1, 0, 0, 0, DateTimeKind.Utc),
            SafetyOperations.TakeRemote, "local", i, i, null)).ToList();
        var drop = SafetyBackupFiles.Prune(entries);
        Assert.Equal(3, drop.Count);
        Assert.DoesNotContain("safety-12.json", drop);
        Assert.Contains("safety-0.json", drop);
    }

    [Fact]
    public void PruneWraps_DropsKeysTheDeviceNoLongerHolds()
    {
        var wraps = new List<SafetyMasterWrap>
        {
            new("live-a", "w1"),
            new("forgotten", "w2"),
            new("live-b", "w3"),
        };

        var kept = SafetyMaster.PruneWraps(wraps, new HashSet<string> { "live-a", "live-b" });

        Assert.Equal(["w1", "w3"], kept.Select(w => w.WrappedKey));
    }

    [Fact]
    public void PruneWraps_NeverLeavesTheMasterUnreadable()
    {
        var wraps = new List<SafetyMasterWrap> { new("gone", "w1") };

        var kept = SafetyMaster.PruneWraps(wraps, new HashSet<string>());

        Assert.Single(kept);
        Assert.Equal("gone", kept[0].KekId);
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

    private static List<BookmarkNode> Other(int count) =>
    [
        new BookmarkNode
        {
            Title = "[xbs] Other",
            Children = Enumerable.Range(0, count)
                .Select(i => new BookmarkNode { Id = 100 + i, Title = $"m{i}", Url = $"https://m{i}.example/" })
                .ToList(),
        },
    ];

    [Fact]
    public void ChangeCounts_SplitAddedAndRemoved()
    {
        Assert.Equal(new SafetyChanges(2, 0), TreeMagnitude.ChangeCounts(Other(3), Other(5)));
        Assert.Equal(new SafetyChanges(0, 12), TreeMagnitude.ChangeCounts(Other(12), Other(0)));
        var c = TreeMagnitude.ChangeCounts(Other(4), Other(1));
        Assert.Equal(c.Added + c.Removed, TreeMagnitude.ChangedNodeCount(Other(4), Other(1)));
    }

    [Fact]
    public void WithSyncNames_UsesUniqueSavedSyncMatch()
    {
        var id = "abcdef0123456789abcdef0123456789";
        var entry = new SafetyBackupEntry("a.json", DateTime.UtcNow, SafetyOperations.SyncPull, "local", 3, 10,
            SafetyBackupFiles.ShortSyncId(id), "master", "Old name", 14, 2);
        var saved = new[] { new SavedSync { ServiceUrl = "https://api.example", SyncId = id, Label = "Work" } };
        var named = SafetyBackupFiles.WithSyncNames([entry], saved).Single();
        Assert.Equal("Work", named.SyncName);
        Assert.Equal(14, named.Added);
        // No (or an ambiguous) match keeps the name recorded at write time.
        Assert.Equal("Old name", SafetyBackupFiles.WithSyncNames([entry], []).Single().SyncName);
        Assert.Equal("abcd…89", entry.SyncId);
    }

    [Fact]
    public void Index_RoundTripsNameAndCounts()
    {
        var entry = new SafetyBackupEntry("a.json", new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc),
            SafetyOperations.SyncPush, "remote", 40, 2048, "abcd…ef", "master", "Home", 12, 3);
        var back = SafetyBackupFiles.ParseIndex(SafetyBackupFiles.SerializeIndex([entry])).Single();
        Assert.Equal(entry, back);
        // Old manifests without the new fields still parse.
        var legacy = SafetyBackupFiles.ParseIndex(
            "[{\"FileName\":\"b.json\",\"CreatedAtUtc\":\"2026-01-01T00:00:00Z\",\"Operation\":\"take-remote\",\"Side\":\"local\",\"Items\":1,\"SizeBytes\":5,\"SyncId\":null}]").Single();
        Assert.Null(legacy.SyncName);
        Assert.Null(legacy.Added);
    }

    [Fact]
    public void DayLabel_TodayYesterdayOrDate()
    {
        var now = new DateTime(2026, 11, 6, 15, 0, 0, DateTimeKind.Local);
        Assert.Equal(Keepmark.Core.I18n.Loc.Get(Keepmark.Core.I18n.Str.SafetyToday),
            SafetyBackupFiles.DayLabel(now.AddHours(-1).ToUniversalTime(), now));
        Assert.Equal(Keepmark.Core.I18n.Loc.Get(Keepmark.Core.I18n.Str.SafetyYesterday),
            SafetyBackupFiles.DayLabel(now.AddDays(-1).ToUniversalTime(), now));
        Assert.Contains("2026", SafetyBackupFiles.DayLabel(now.AddDays(-5).ToUniversalTime(), now));
    }
}
