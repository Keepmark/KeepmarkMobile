# SQLCipher native distribution

Keepmark 1.0.195 uses SQLCipher Community 4.19.0 (SQLite 3.53.4).
The managed adapter is SQLitePCLRaw.provider.sqlcipher 3.0.5; sqlite-net-base
1.9.172 supplies the ORM without selecting another native bundle.

## Android

The checked-in `android/{arm64-v8a,x86_64}/libsqlcipher.so` files are extracted
unchanged from Zetetic's official sqlcipher-android 4.19.1 AAR. The unchanged
`classes.jar` is extracted as `android/sqlcipher-android.jar` and packaged with
`AndroidLibrary Bind="false"`. Although .NET uses the C API, the native library
registers Java classes in `JNI_OnLoad`; omitting them aborts startup with
`SQLiteCustomFunction` missing. SQLCipher ProGuard rules keep the native
registration roots in shrunk builds. `Xamarin.AndroidX.Sqlite` 2.6.2 supplies
the AndroidX Java interfaces implemented by the official runtime; it does not
replace the SQLCipher engine. This preserves
the existing two supported ABIs. Both ELF libraries support 16 KiB pages.

`sources.json` pins the download URLs, archive hashes and individual library
hashes. To reproduce extraction and verify the final APK:

```sh
python3 scripts/prepare-sqlcipher.py --android
python3 scripts/verify-native-sqlcipher.py --apk /path/to/app.apk
```

For a device-specific Debug APK, add `--abi arm64-v8a` (or `--abi x86_64`);
without `--abi` the verifier requires both supported ABIs in the APK.

The verifier checks exact Java/native hashes, every SQLCipher Java class
definition in APK DEX (not just string references), ELF architecture/alignment, ZIP
alignment and uncompressed storage, and rejects legacy/plain SQLite natives.
The Community binaries use LibTomCrypt. `THIRD-PARTY-NOTICES.txt` contains the
SQLCipher BSD-style notice, SQLite public-domain notice and LibTomCrypt notice
(public-domain option); this file is also bundled as an Android asset.
No commercial package or license is required for this distribution.

## Linux development

Tests and benchmarks currently support Linux x64. Install Python 3.12+, GCC,
make, Tcl, pkg-config and OpenSSL development headers/libraries, then run:

```sh
python3 scripts/prepare-sqlcipher.py --linux
dotnet test
```

The script verifies the pinned SQLCipher source archive and builds it with
system OpenSSL, FTS5, in-memory temporary storage and the SQLCipher codec.
All downloads/build output are ignored under `artifacts/native/`.
There is no automatic network access or native compilation during `dotnet test`;
a missing development library produces an explicit build error.

## Compatibility and rollback

No rekey, schema-format conversion or destructive migration is performed.
Core tests open and modify the original synthetic SQLCipher 4.5.2 fixture
(`tests/Keepmark.Core.Tests/TestData/sqlcipher-2.1.10.db`) with the new engine.
For proof in both directions, using the previous cached native library:

```sh
python3 scripts/verify-sqlcipher-compat.py --old /path/to/old/libe_sqlcipher.so
```

The probe runs isolated processes: new-write, old-read/write, new-read, including
FTS. Rollback restores the previous application/provider/native stack; existing
databases need no conversion. Keep the original fixture unchanged.
Host compatibility tests and final APK checks passed. On 2026-10-16, v1.0.195
Debug was installed over the existing app on an Android 15 arm64 phone: startup
and the existing encrypted bookmark store opened successfully. The final APK
contains all 63 SQLCipher Java classes, the AndroidX interfaces and the unchanged
arm64 native binary. Release shrinking and battery measurements remain unverified.

Primary references: [SQLCipher source](https://github.com/sqlcipher/sqlcipher/tree/v4.19.0),
[official Android release](https://repo.maven.apache.org/maven2/net/zetetic/sqlcipher-android/4.19.1/),
[SQLCipher compatibility](https://www.zetetic.net/sqlcipher/compatibility/).
