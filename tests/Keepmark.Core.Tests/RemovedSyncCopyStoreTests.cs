using System.Text.Json;
using Keepmark.Core.Crypto;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;
using Keepmark.Core.Safety;

namespace Keepmark.Core.Tests;

/// <summary>The removed-sync copy pipeline (validate → encrypt → write →
/// verify → list/read → delete) is content-addressed, path-guarded and never
/// loses a good file to a damaged one.</summary>
public sealed class RemovedSyncCopyStoreTests : IDisposable
{
    private static readonly string Key = Convert.ToBase64String(new byte[32]);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"xbs-removed-{Guid.NewGuid():N}");
    private readonly XCryptoService _crypto = new(new XbsLocalizer(), new InMemoryLogger());
    private readonly InMemoryLogger _log = new();
    private readonly RemovedSyncCopyStore _store;

    public RemovedSyncCopyStoreTests()
    {
        _store = new RemovedSyncCopyStore(_dir, _crypto, () => Task.FromResult(Key),
            new XbsLocalizer(), _log);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static List<BookmarkNode> Tree(params (int Id, string Title, string? Url)[] items) =>
        items.Select(i => new BookmarkNode { Id = i.Id, Title = i.Title, Url = i.Url }).ToList();

    private static List<BookmarkNode> Sample() => Tree(
        (1, "Dev", null),
        (2, "GitHub", "https://github.com"));

    [Fact]
    public async Task Keep_WritesOneVerifiedFileNamedByTheTreeHash()
    {
        var kept = await _store.KeepAsync("https://example.test", Sample());

        Assert.NotNull(kept);
        Assert.Equal(2, kept!.Value.Items);
        var files = Directory.GetFiles(_dir);
        Assert.Equal(kept.Value.FileName, Assert.Single(files).Split(Path.DirectorySeparatorChar)[^1]);
        Assert.EndsWith(".json", files[0]);

        var copy = await _store.ReadAsync(kept.Value.FileName);
        Assert.Equal(RemovedSyncCopies.CurrentFormat, copy.Format);
        Assert.Equal("https://example.test", copy.ServiceUrl);
        Assert.Equal(2, copy.Items);
        Assert.Equal(
            RemovedSyncCopies.Hash(JsonSerializer.Serialize(Sample())),
            RemovedSyncCopies.Hash(JsonSerializer.Serialize(copy.Tree)));
    }

    [Fact]
    public async Task Keep_SameTreeTwice_KeepsASingleFile()
    {
        var first = await _store.KeepAsync("https://example.test", Sample());
        var second = await _store.KeepAsync("https://example.test", Sample());

        Assert.Equal(first!.Value.FileName, second!.Value.FileName);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Keep_SameTreeFromAnotherSync_RelabelsInsteadOfDuplicating()
    {
        var first = await _store.KeepAsync("https://first.example", Sample());
        var second = await _store.KeepAsync("https://second.example", Sample());

        Assert.Equal(first!.Value.FileName, second!.Value.FileName);
        Assert.Single(Directory.GetFiles(_dir));
        var copy = await _store.ReadAsync(first.Value.FileName);
        Assert.Equal("https://second.example", copy.ServiceUrl);
    }

    [Fact]
    public async Task Keep_EmptyTree_KeepsNothing()
    {
        Assert.Null(await _store.KeepAsync("https://example.test", []));
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task Keep_EmptyTree_IsRejectedBeforeAnyFileIsWritten()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.KeepAsync("https://example.test", [null!]));
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task List_SkipsDamagedAndForeignFilesWithoutDeletingThem()
    {
        var kept = await _store.KeepAsync("https://example.test", Sample());
        var broken = Path.Combine(_dir, RemovedSyncCopies.FilePrefix + "broken.json");
        await File.WriteAllTextAsync(broken, "not-an-envelope");
        var foreign = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(foreign, "keep me");
        var other = Path.Combine(_dir, RemovedSyncCopies.FilePrefix + "other.json");
        await File.WriteAllTextAsync(other, _crypto.Encrypt("{}", Convert.ToBase64String(new byte[32])));

        var list = await _store.ListAsync();

        var single = Assert.Single(list);
        Assert.Equal(kept!.Value.FileName, single.FileName);
        Assert.Equal(2, single.Items);
        Assert.True(File.Exists(broken));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(other));
        Assert.Contains(_log.Entries, e => e.Level == XbsLogLevel.Warning);
    }

    [Fact]
    public async Task List_OrdersNewestFirst()
    {
        var older = await _store.KeepAsync("https://older.example", Tree((1, "A", "https://a.example")));
        var newer = await _store.KeepAsync("https://newer.example", Tree((2, "B", "https://b.example")));
        var newerPath = Path.Combine(_dir, newer!.Value.FileName);
        var olderPath = Path.Combine(_dir, older!.Value.FileName);
        File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddHours(-2));

        var list = await _store.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, c => c.FileName == newer.Value.FileName);
        Assert.Contains(list, c => c.FileName == older.Value.FileName);
        Assert.True(list[0].CreatedAtUtc >= list[1].CreatedAtUtc);
    }

    [Fact]
    public async Task Read_WithAnotherDeviceKey_FailsAndDoesNotList()
    {
        var kept = await _store.KeepAsync("https://example.test", Sample());
        var stranger = new XCryptoService(new XbsLocalizer(), new InMemoryLogger());
        var other = new RemovedSyncCopyStore(_dir, stranger,
            () => Task.FromResult(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            new XbsLocalizer(), new InMemoryLogger());

        Assert.Empty(await other.ListAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => other.ReadAsync(kept!.Value.FileName));
        Assert.True(File.Exists(Path.Combine(_dir, kept.Value.FileName)));
    }

    [Fact]
    public async Task Read_UnknownFile_ThrowsThePrunedMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.ReadAsync(RemovedSyncCopies.FileName(new string('a', 64))));
        Assert.Equal(new XbsLocalizer().Get(Str.ErrBackupPruned), ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape.json")]
    [InlineData("/etc/passwd")]
    [InlineData("notes.txt")]
    [InlineData("removed-.txt")]
    public void PathFor_RejectsAnythingOutsideTheDirectory(string name)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _store.PathFor(name));
        Assert.Equal(new XbsLocalizer().Get(Str.ErrUnknownBackup), ex.Message);
    }

    [Fact]
    public void Delete_RemovesTheFileAndIsNotRepeatable()
    {
        var kept = _store.KeepAsync("https://example.test", Sample()).GetAwaiter().GetResult();

        _store.Delete(kept!.Value.FileName);

        Assert.Empty(Directory.GetFiles(_dir));
        Assert.ThrowsAny<InvalidOperationException>(() => _store.Delete(kept.Value.FileName));
    }

    [Fact]
    public async Task List_MissingDirectory_IsEmpty()
    {
        Assert.Empty(await _store.ListAsync());
    }
}
