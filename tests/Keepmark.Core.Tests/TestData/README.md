# Test fixtures

`vectors.json` contains the xBrowserSync Node crypto interoperability vectors.

`sqlcipher-2.1.10.db` is synthetic, not user data. It was created by the original
Core store at `acf6b123815cde9743cd80f942cf85d2f26be9c6` with sqlite-net-pcl 1.9.172
and SQLitePCLRaw.bundle_e_sqlcipher 2.1.10 (SQLCipher 4.5.2 / SQLite 3.39.2).
Its test-only raw key is `0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef`.
It holds one folder, one bookmark, FTS, legacy blob/meta and v1.2 revision/hash.
`StorePerformanceTests.NativeSqlcipher419_OpensAndUpdatesEncryptedDatabaseFrom210`
copies it into a temporary file, verifies reads/FTS/metadata, modifies it,
reopens it and verifies that an independent unkeyed connection cannot read it.
The committed fixture must not be rewritten with the new engine.
