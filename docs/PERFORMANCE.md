# Performance and battery

Living document: behavior that is designed to stay cheap, the desktop
measurements behind the big optimizations, and what still needs a device. The
numbers are desktop indicators, not Android predictions: cold startup, WebView
frame time, memory residency and idle battery must be measured on hardware.

## Sync behavior

The local library paints before derived Home data is loaded. Startup shares a
single initialization task rather than 100 ms readiness polling. Component
lifetimes prevent late subscriptions and timers. Auto synchronization runs only
while the window is visible; its first check is delayed 500 ms to let local
content paint, and later checks wait 15 minutes **after the previous tick
completes** (fixed delay, no catch-up storm). Manual mode has no poll.

Stopping cancels reads and retries; a PUT already sent by the edit worker may
complete, to preserve its outcome. Dirty edits remain durable and are checked on
resume. One 250 ms edit worker coalesces uploads and checks
dirty/conflict/mode/visibility under the engine gate.

Successful ordinary uploads update metadata/blob and only changed normalized
descriptions, without deleting and reinserting every Item or rebuilding FTS.
Pull and restore keep their full-replacement semantics. Creating, activating or
adding a saved sync costs one `/info` probe, one full GET and one decrypt — no
background polling of dormant rows happens at all.

## Store and rendering

Store lookups use indexes, targeted ancestor queries and bulk reads/deletes.
Folder counts aggregate bottom-up; depth/path memoization avoids repeated
ancestor walks. Home reuses derived data until the store generation, culture,
pins or settings change. The duplicate badge uses SQL aggregation.

Uniform collapsed folder lists above 100 rows virtualize; duplicate groups use
pages of 100 rows; separators or expanded Home rows use the ordinary renderer;
explicit Home location jumps render the complete target folder until navigation,
to retain scroll anchors. Selection resolves ids and validates inside the same
transaction as the mutation, so a batch never walks the tree twice.

Safety backups are written once per destructive operation (plus one index
rewrite), never per item; removed-sync copies are content-addressed, so keeping
the same tree twice costs no extra file.

## Memory bounds

Compression history uses bounded hash chains (~384 KiB maximum tables);
decompression sizes and validates output before allocating. Backup envelopes are
deserialized from a stream; base64 validation does not allocate decoded copies.

| Limit | Value |
|---|---|
| Backup file | 50 MiB |
| Decoded encrypted envelope | 50 MiB |
| Plaintext UTF-8 tree | 256 MiB |
| HTTP JSON response | 72 MiB (fits base64 envelope plus metadata) |
| HTTP error body | 64 KiB |

These are device resource limits, independent of a server's configured quota.
Extremely compressible payloads may be rejected even below the encrypted-file
cap.

## Desktop measurements

Measured against baseline `acf6b123815cde9743cd80f942cf85d2f26be9c6`, Linux x64,
Intel i5-1145G7, Release .NET 10, SQLCipher. Synthetic valid tree has 32 folder
levels, bookmarks distributed between those levels, and 50 matching search rows.
One warmup then five samples, reported median; full GC outside measured intervals.
Allocations use `GC.GetTotalAllocatedBytes`, so background runtime activity may
contribute.

| Nodes | Operation | Baseline ms | Optimized ms |
|---:|---|---:|---:|
| 1,000 | Counts | 2.535 | 1.851 |
| 1,000 | Search 50 results | 5.251 | 0.898 |
| 1,000 | Paths for 50 rows | 4.552 | 0.405 |
| 1,000 | Open folder | 0.261 | 0.281 |
| 10,000 | Counts | 19.794 | 13.825 |
| 10,000 | Search 50 results | 54.330 | 1.043 |
| 10,000 | Paths for 50 rows | 66.814 | 0.502 |
| 10,000 | Open folder | 1.865 | 1.056 |
| 50,000 | Counts | 104.062 | 95.081 |
| 50,000 | Search 50 results | 197.787 | 0.903 |
| 50,000 | Paths for 50 rows | 197.901 | 0.390 |
| 50,000 | Open folder | 38.338 | 3.782 |

At 50,000 nodes, allocations per search fell from 60,866,120 to 88,104 bytes;
targeted paths from 60,842,216 to 60,408 bytes; counts from 11,239,280 to
5,570,272 bytes. Folder query allocations remained 400,184 bytes (same rows).
A repeated baseline count run took 92.603 ms: count timing differences are within
observed variability; claim allocation reduction rather than guaranteed latency.
Small-folder timings likewise show no reliable benefit.

A separate single-sample desktop Debug compression check on deterministic 512 KiB
ASCII reduced allocations from 15,525,360 to 1,441,760 bytes and elapsed time from
281.1 to 43.6 ms, with identical output length. Treat timing as illustrative,
not an Android prediction or a statistical benchmark.

## Verification

Run from `KeepmarkMobile/`:

```sh
python3 scripts/prepare-sqlcipher.py --linux
dotnet test tests/Keepmark.Core.Tests/Keepmark.Core.Tests.csproj
node --test tests/js/gestures.test.cjs
dotnet run --project tools/Keepmark.Benchmarks -c Release
# Reverse crypto interop; lzutf8@0.6.0 from the sibling extension or LZUTF8_MODULE.
dotnet run --project tools/Keepmark.Benchmarks -c Release -- --interop /tmp/keepmark-csharp-blobs.json
node scripts/verify-csharp-blobs.cjs /tmp/keepmark-csharp-blobs.json
```

The Core suite is **370 tests**. Existing JSON vectors test Node→C#. The reverse
script decrypts C# output with Node AES-GCM and lzutf8@0.6.0 for ASCII, Unicode,
bookmarks and a larger payload. Regression tests cover worker coalescing, edits
during upload, stable RowIds, SQL query plans, targeted paths, deep
aggregation/delete/undo, lifecycle cancellation/drain, response limits/timeouts
and bounded compression allocation.

## Still requires a device

Android Debug and Release builds validate compilation and packaging. Real
validation is still needed for cold startup, WebView virtualization with variable
row heights, scroll anchors, 30–60 minute idle battery usage, long navigation
memory stability, QR camera behavior and TalkBack/font scaling. No device access
is required for the automated checks above.

## Native SQLCipher engine

`sqlite-net-base` 1.9.172 plus `SQLitePCLRaw.provider.sqlcipher` 3.0.5 load
SQLCipher 4.19.0 / SQLite 3.53.4. Android uses the official Community 4.19.1 AAR
binaries; Linux tests compile the same engine source with OpenSSL. The old
encrypted fixture still opens, and a separate bidirectional compatibility probe
verifies new-engine writes with the old 4.5.2 engine and vice versa, including
FTS. The Release APK is checked for exact official hashes, both ABIs and 16 KiB
ELF/ZIP alignment. No database conversion is needed.

This engine upgrade makes no claimed speed or battery improvement; the desktop
measurements above describe the code optimizations.
