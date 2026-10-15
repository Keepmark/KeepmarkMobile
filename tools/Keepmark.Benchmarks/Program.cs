using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Keepmark.Core.Crypto;
using Keepmark.Core.I18n;
using Keepmark.Core.Data;
using Keepmark.Core.Logging;
using Keepmark.Core.Models;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args is ["--interop", var outputPath])
{
    using var vectors = JsonDocument.Parse(File.ReadAllText("tests/Keepmark.Core.Tests/TestData/vectors.json"));
    var derivedKey = vectors.RootElement.GetProperty("derivedKey").GetString()!;
    var crypto = new XCryptoService(new XbsLocalizer(), new InMemoryLogger());
    var cases = vectors.RootElement.GetProperty("cases").EnumerateArray().Select(v =>
    {
        var plain = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(v.GetProperty("plainB64").GetString()!));
        return new { name = v.GetProperty("name").GetString()!, plain, blob = crypto.Encrypt(plain, derivedKey) };
    }).ToList();
    var large = string.Concat(Enumerable.Repeat("abcabcabcàèìòù日本語🎉", 20_000));
    cases.Add(new { name = "large", plain = large, blob = crypto.Encrypt(large, derivedKey) });
    File.WriteAllText(outputPath, JsonSerializer.Serialize(new { key = derivedKey, cases }));
    return;
}
Console.WriteLine("nodes,operation,median_ms,median_allocated_bytes");
const string key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
foreach (int size in new[] { 1000, 10000, 50000 })
{
    var path = Path.Combine(Path.GetTempPath(), $"keepmark-bench-{Guid.NewGuid():N}.db");
    await using var store = new BookmarkStore(path, key, new InMemoryLogger());
    await store.InitializeAsync();
    var roots = new List<BookmarkNode>();
    var folders = new List<BookmarkNode>();
    BookmarkNode? parent = null;
    const int depth = 32;
    for (int i = 0; i < depth; i++)
    {
        var folder = new BookmarkNode { Id = i + 1, Title = $"Folder {i}", Children = [] };
        if (parent is null) roots.Add(folder); else parent.Children!.Add(folder);
        folders.Add(folder);
        parent = folder;
    }
    for (int i = depth; i < size; i++)
    {
        folders[i % depth].Children!.Add(new BookmarkNode
        {
            Id = i + 1, Title = i < depth + 50 ? $"Needle {i}" : $"Bookmark {i}",
            Url = $"https://example.test/{i}"
        });
    }
    await store.ReplaceAllAsync(roots, "blob", "t", "v");
    var targetRows = (await store.GetByNodeIdsAsync(Enumerable.Range(depth + 1, 50))).Select(r => r.RowId).ToArray();
    var parentId = (await store.GetByNodeIdsAsync([depth])).Single().RowId;
    await Measure("counts", size, async () => { _ = await store.GetFolderBookmarkCountsAsync(); });
    await Measure("search50", size, async () => { _ = await store.SearchAsync("Needle"); });
    await Measure("paths50", size, async () => {
        _ = await store.GetFolderPathsAsync(targetRows);
    });
    await Measure("children", size, async () => { _ = await store.GetChildrenAsync(parentId); });
    await store.DisposeAsync();
    File.Delete(path);
}

static async Task Measure(string label, int size, Func<Task> action)
{
    await action();
    var elapsed = new List<double>();
    var allocated = new List<long>();
    for (int i = 0; i < 5; i++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();
        elapsed.Add(sw.Elapsed.TotalMilliseconds);
        allocated.Add(GC.GetTotalAllocatedBytes(true) - before);
    }
    elapsed.Sort(); allocated.Sort();
    Console.WriteLine($"{size},{label},{elapsed[2]:F3},{allocated[2]}");
}
