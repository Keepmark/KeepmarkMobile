using Keepmark.Core.Safety;
using Keepmark.Core.Sync;
using Xunit;

namespace Keepmark.Core.Tests;

public class SavedSyncsTests
{
    private static readonly string KeyA = Convert.ToBase64String(new byte[32]);
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
    private static readonly DateTimeOffset T1 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);

    private static SavedSync Row(
        string url = "https://api.example.org",
        string id = "id-1",
        string? label = null,
        DateTimeOffset? lastUsed = null,
        string key = "",
        string? version = null,
        string? keyId = null) => new()
    {
        ServiceUrl = url,
        SyncId = id,
        Label = label,
        LastUsedAt = lastUsed ?? T1,
        DerivedKey = key,
        SyncVersion = version,
        KeyId = keyId
    };

    [Fact]
    public void SameIdentityIgnoresHostCaseTrailingSlashAndDefaultPort()
    {
        Assert.True(SavedSyncs.Same(
            new("https://API.Example.org/", "abc"),
            new("https://api.example.org:443", "abc")));
        Assert.True(SavedSyncs.Same(
            new("https://example.org/proxy/", "abc"),
            new("https://example.org/proxy", "abc")));
        Assert.False(SavedSyncs.Same(
            new("https://example.org", "abc"),
            new("https://example.org", "abd")));
        Assert.False(SavedSyncs.Same(
            new("https://example.org/api", "abc"),
            new("https://example.org/other", "abc")));
    }

    [Fact]
    public void IsActiveHandlesMissingActiveSync()
    {
        var row = Row();
        Assert.False(SavedSyncs.IsActive(row.Identity, null));
        Assert.True(SavedSyncs.IsActive(row.Identity, new("https://API.example.org/", "id-1")));
    }

    [Fact]
    public void UpsertDoesNotDuplicateAndKeepsLabelVersionAndKey()
    {
        var list = SavedSyncs.Upsert([], Row(label: "Work", key: KeyA, version: "1.1.13"));
        Assert.Single(list);
        Assert.Equal("https://api.example.org", list[0].ServiceUrl);
        Assert.Equal("Work", list[0].Label);

        // Refresh from a canonical variant, no label/version/key carried.
        list = SavedSyncs.Upsert(list, Row(url: "https://API.example.org/", lastUsed: T2));
        Assert.Single(list);
        Assert.Equal("Work", list[0].Label);
        Assert.Equal("1.1.13", list[0].SyncVersion);
        Assert.Equal(KeyA, list[0].DerivedKey);
        Assert.Equal(T2, list[0].LastUsedAt);
    }

    [Fact]
    public void UpsertDescendingAndDeterministic()
    {
        var list = SavedSyncs.Upsert([], Row(id: "b", label: "Beta", lastUsed: T1));
        list = SavedSyncs.Upsert(list, Row(id: "a", label: "Beta", lastUsed: T1));
        list = SavedSyncs.Upsert(list, Row(id: "c", label: "Alpha", lastUsed: T2));

        Assert.Equal(new[] { "c", "a", "b" }, list.Select(r => r.SyncId));
    }

    [Fact]
    public void FindAndRemoveUseCanonicalIdentity()
    {
        var list = new List<SavedSync> { Row(id: "keep"), Row(id: "drop") };
        var target = new SavedSyncIdentity("https://API.example.org/", "drop");

        Assert.Equal("drop", SavedSyncs.Find(list, target)?.SyncId);
        var remaining = SavedSyncs.Remove(list, target);
        Assert.Single(remaining);
        Assert.Equal("keep", remaining[0].SyncId);
        Assert.Null(SavedSyncs.Find(remaining, target));
        Assert.Empty(SavedSyncs.Remove([], target));
    }

    [Fact]
    public void LabelsAreTrimmedCappedAndUniqueCaseInsensitively()
    {
        Assert.Null(SavedSyncs.NormalizeLabel("   "));
        Assert.Null(SavedSyncs.NormalizeLabel(null));
        Assert.Equal("Work", SavedSyncs.NormalizeLabel("  Work "));
        Assert.Equal(SavedSyncs.MaxLabelLength, SavedSyncs.NormalizeLabel(new string('x', 60))!.Length);

        var list = new List<SavedSync> { Row(id: "a", label: "Work"), Row(id: "b", label: "Home") };
        Assert.True(SavedSyncs.LabelTaken(list, null, "  wORK "));
        Assert.False(SavedSyncs.LabelTaken(list, new("https://api.example.org", "a"), "Work"));
        Assert.False(SavedSyncs.LabelTaken(list, null, "Other"));
        Assert.False(SavedSyncs.LabelTaken(list, null, "   "));
        // The displayed name counts, host fallback included: two rows both
        // showing "api.example.org" would be indistinguishable.
        Assert.True(SavedSyncs.LabelTaken([Row()], null, "API.example.org"));
        Assert.False(SavedSyncs.LabelTaken([Row()], new("https://api.example.org", "id-1"), "api.example.org"));
    }

    [Fact]
    public void NameFallsBackToHostAndShortIdHidesTheToken()
    {
        Assert.Equal("service.example.net", SavedSyncs.Name(Row(url: "https://service.example.net:8443/x")));
        Assert.Equal("Work", SavedSyncs.Name(Row(label: " Work ")));
        Assert.Equal("not a url", SavedSyncs.Name(Row(url: "not a url")));

        Assert.Equal("abc", SavedSyncs.ShortSyncId("abc"));
        Assert.Equal("abcdefgh…", SavedSyncs.ShortSyncId("abcdefghijklmnop"));
        Assert.Equal(8, SavedSyncs.ShortSyncId("abcdefghijklmnop").Length - 1);
    }

    [Fact]
    public void SuggestNameAddsSuffixWhenTaken()
    {
        var list = new List<SavedSync>
        {
            Row(id: "a", label: "api.example.org"),
            Row(id: "b", label: "api.example.org 2")
        };
        Assert.Equal("api.example.org 3", SavedSyncs.SuggestName("https://api.example.org", list));
        // Excluding a row frees the name it was holding.
        Assert.Equal("api.example.org", SavedSyncs.SuggestName("https://api.example.org", list,
            new SavedSyncIdentity("https://api.example.org", "a")));
        Assert.Equal("other.example", SavedSyncs.SuggestName("https://other.example/", list));
    }

    [Fact]
    public void DeserializeSurvivesDirtyJson()
    {
        const string json = """
        [
          {"serviceUrl":"https://EXAMPLE.com/","syncId":"abc","label":"Work","lastUsedAt":"2026-01-02T03:04:05Z","derivedKey":"k"},
          {"syncId":"missing-url"},
          {"serviceUrl":"https://x.example","syncId":"   "},
          "not-an-object",
          {"serviceUrl":"https://x.example","syncId":"id2","lastUsedAt":"garbage"},
          {"serviceUrl":42,"syncId":"id3"},
          {"serviceUrl":"https://x.example","syncId":"id4","label":"   ","lastUsedAt":null}
        ]
        """;
        var rows = SavedSyncs.Deserialize(json);

        Assert.Equal(new[] { "abc", "id2", "id4" }, rows.Select(r => r.SyncId));
        Assert.Equal("https://example.com", rows[0].ServiceUrl);
        Assert.Equal("Work", rows[0].Label);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), rows[0].LastUsedAt);
        Assert.Equal(default, rows[1].LastUsedAt);
        Assert.Null(rows[2].Label);

        Assert.Empty(SavedSyncs.Deserialize(null));
        Assert.Empty(SavedSyncs.Deserialize(""));
        Assert.Empty(SavedSyncs.Deserialize("{not json"));
        Assert.Empty(SavedSyncs.Deserialize("""{"syncUrl":"not-an-array"}"""));

        // Labels are normalized on read too: a hand-edited JSON never yields
        // a padded/overlong name that the rename path would refuse.
        var padded = SavedSyncs.Deserialize(
            """[{"serviceUrl":"https://x.example","syncId":"id9","label":"  Work  "}]""");
        Assert.Equal("Work", Assert.Single(padded).Label);
    }

    [Fact]
    public void DeserializeDeduplicatesKeepingMostRecentRow()
    {
        const string json = """
        [
          {"serviceUrl":"https://example.com/","syncId":"abc","label":"Old","lastUsedAt":"2026-01-02T00:00:00Z"},
          {"serviceUrl":"https://example.com","syncId":"abc","label":"New","lastUsedAt":"2026-03-02T00:00:00Z"}
        ]
        """;
        var row = Assert.Single(SavedSyncs.Deserialize(json));
        Assert.Equal("New", row.Label);
        Assert.Equal(DateTimeOffset.Parse("2026-03-02T00:00:00Z"), row.LastUsedAt);
    }

    [Fact]
    public void SerializeRoundTripsAndOmitsEmptyFields()
    {
        var rows = new List<SavedSync>
        {
            Row(id: "a", label: "Work", key: KeyA, version: "1.2.0", keyId: "fp", lastUsed: T2),
            Row(id: "b", lastUsed: T1)
        };
        var json = SavedSyncs.Serialize(rows);
        Assert.DoesNotContain("\"label\":null", json);
        Assert.DoesNotContain("\"derivedKey\":null", json);

        var back = SavedSyncs.Deserialize(json);
        Assert.Equal(rows, back);
    }
}

public class SavedSyncRegistryTests
{
    private static readonly string KeyA = Convert.ToBase64String(new byte[32]);
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Store(SyncCredentials? creds) : ICredentialStore
    {
        public SyncCredentials? Creds { get; set; } = creds;
        public List<SavedSync> Saved { get; set; } = [];
        public int Writes { get; private set; }
        public Task<SyncCredentials?> GetAsync() => Task.FromResult(Creds);
        public Task SetAsync(SyncCredentials c) { Creds = c; return Task.CompletedTask; }
        public Task ClearAsync() { Creds = null; return Task.CompletedTask; }
        public Task<List<SavedSync>> GetSavedSyncsAsync() => Task.FromResult(Saved.ToList());
        public Task SetSavedSyncsAsync(IReadOnlyList<SavedSync> rows)
        {
            Saved = rows.ToList();
            Writes += 1;
            return Task.CompletedTask;
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Yields on every read/write so concurrent callers interleave
    /// exactly where a read-modify-write would lose a row.</summary>
    private sealed class SlowStore : ICredentialStore
    {
        private readonly object _gate = new();
        private List<SavedSync> _rows = [];
        public Task<SyncCredentials?> GetAsync() => Task.FromResult<SyncCredentials?>(null);
        public Task SetAsync(SyncCredentials c) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
        public async Task<List<SavedSync>> GetSavedSyncsAsync()
        {
            await Task.Yield();
            lock (_gate) return _rows.ToList();
        }
        public async Task SetSavedSyncsAsync(IReadOnlyList<SavedSync> rows)
        {
            await Task.Yield();
            lock (_gate) _rows = rows.ToList();
        }
    }

    private static SavedSyncRegistry Registry(Store store, DateTimeOffset? now = null) =>
        new(store, new Clock(now ?? Now));

    [Fact]
    public async Task ConcurrentWritesNeverLoseARow()
    {
        var store = new SlowStore();
        var registry = new SavedSyncRegistry(store, new Clock(Now));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            registry.RememberAsync(new SavedSync
            {
                ServiceUrl = $"https://service{i}.example",
                SyncId = $"id-{i}",
                DerivedKey = KeyA,
                LastUsedAt = Now.AddMinutes(i)
            })));

        Assert.Equal(20, (await store.GetSavedSyncsAsync()).Count);
    }

    [Fact]
    public async Task SyncActiveMigratesLegacyCredentialsOnce()
    {
        var store = new Store(new SyncCredentials("sync-1", "https://API.example.org/", KeyA, "1.1.13"));
        var registry = Registry(store);

        var row = await registry.SyncActiveAsync(store.Creds);

        Assert.NotNull(row);
        Assert.Equal("sync-1", row!.SyncId);
        Assert.Equal("https://api.example.org", row.ServiceUrl);
        Assert.Equal(KeyA, row.DerivedKey);
        Assert.Equal("1.1.13", row.SyncVersion);
        Assert.Equal(SafetyMaster.Fingerprint(KeyA), row.KeyId);
        Assert.Equal(Now, row.LastUsedAt);
        Assert.Single(store.Saved);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task SyncActiveOnRestartKeepsListOrderAndDoesNotRewrite()
    {
        var store = new Store(new SyncCredentials("sync-1", "https://api.example.org", KeyA, "1.1.13"));
        var registry = Registry(store);
        await registry.SyncActiveAsync(store.Creds);
        var writes = store.Writes;

        var clock = new Clock(Now.AddDays(30));
        var again = await new SavedSyncRegistry(store, clock)
            .SyncActiveAsync(new SyncCredentials("sync-1", "https://api.example.org/", KeyA, "1.1.13"));

        Assert.Equal(Now, again!.LastUsedAt);
        Assert.Equal(writes, store.Writes);
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task SyncActiveRefreshesKeyVersionAndLabelIsPreserved()
    {
        var store = new Store(new SyncCredentials("sync-1", "https://api.example.org", KeyA, "1.1.13"));
        var registry = Registry(store);
        var row = await registry.SyncActiveAsync(store.Creds);
        await registry.RenameAsync(row!.Identity, "Work");

        var updated = await registry.SyncActiveAsync(
            new SyncCredentials("sync-1", "https://api.example.org", KeyB, "1.2.0"));

        Assert.Equal(KeyB, updated!.DerivedKey);
        Assert.Equal("1.2.0", updated.SyncVersion);
        Assert.Equal(SafetyMaster.Fingerprint(KeyB), updated.KeyId);
        Assert.Equal("Work", updated.Label);
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task SyncActiveWithoutCredentialsLeavesRegistryAlone()
    {
        var store = new Store(null);
        var registry = Registry(store);
        Assert.Null(await registry.SyncActiveAsync(null));
        Assert.Empty(store.Saved);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task RememberDeduplicatesAndKeepsEarlierLabel()
    {
        var store = new Store(null);
        var registry = Registry(store);
        await registry.RememberAsync(new SavedSync
        {
            ServiceUrl = "https://one.example/", SyncId = "a", DerivedKey = KeyA, Label = "Work", LastUsedAt = Now
        });
        await registry.RememberAsync(new SavedSync
        {
            ServiceUrl = "https://ONE.example", SyncId = "a", DerivedKey = KeyA, LastUsedAt = Now.AddDays(1)
        });

        var row = Assert.Single(store.Saved);
        Assert.Equal("Work", row.Label);
        Assert.Equal(Now.AddDays(1), row.LastUsedAt);
        Assert.Equal(SafetyMaster.Fingerprint(KeyA), row.KeyId);
    }

    [Fact]
    public async Task ForgetsOnlyTheRequestedDormantRow()
    {
        var store = new Store(null);
        var registry = Registry(store);
        await registry.RememberAsync(new SavedSync { ServiceUrl = "https://one.example", SyncId = "a", DerivedKey = KeyA, LastUsedAt = Now });
        await registry.RememberAsync(new SavedSync { ServiceUrl = "https://one.example", SyncId = "b", DerivedKey = KeyB, LastUsedAt = Now });

        await registry.ForgetAsync(new SavedSyncIdentity("https://ONE.example/", "a"));

        var row = Assert.Single(await registry.ListAsync());
        Assert.Equal("b", row.SyncId);
    }

    [Fact]
    public async Task RenameSetsClearsAndRejectsDuplicates()
    {
        var store = new Store(null);
        var registry = Registry(store);
        await registry.RememberAsync(new SavedSync { ServiceUrl = "https://one.example", SyncId = "a", DerivedKey = KeyA, Label = "Work", LastUsedAt = Now });
        await registry.RememberAsync(new SavedSync { ServiceUrl = "https://one.example", SyncId = "b", DerivedKey = KeyB, LastUsedAt = Now });
        var identity = new SavedSyncIdentity("https://one.example", "b");

        Assert.False(await registry.RenameAsync(identity, " work "));
        Assert.Equal("Work", (await registry.FindAsync(new SavedSyncIdentity("https://one.example", "a")))!.Label);

        Assert.True(await registry.RenameAsync(identity, "  Home  "));
        Assert.Equal("Home", (await registry.FindAsync(identity))!.Label);

        Assert.True(await registry.RenameAsync(identity, "   "));
        Assert.Null((await registry.FindAsync(identity))!.Label);

        Assert.False(await registry.RenameAsync(new SavedSyncIdentity("https://one.example", "missing"), "x"));
    }

    [Fact]
    public async Task ListReturnsMostRecentlyUsedFirst()
    {
        var store = new Store(null);
        var registry = Registry(store);
        await registry.RememberAsync(new SavedSync { ServiceUrl = "https://one.example", SyncId = "old", DerivedKey = KeyA, LastUsedAt = Now });
        await registry.RememberAsync(new SavedSync { ServiceUrl = "https://two.example", SyncId = "new", DerivedKey = KeyB, LastUsedAt = Now.AddMinutes(5) });

        Assert.Equal(new[] { "new", "old" }, (await registry.ListAsync()).Select(r => r.SyncId));
    }
}
