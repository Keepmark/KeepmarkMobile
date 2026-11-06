# Keepmark for Android — technical guide

Reference for contributors and reviewers: requirements, build environment,
architecture, data and crypto, sync engine, multi-sync behavior, safety
backups, UI rules, tests and release steps.

Product overview: [README](../README.md). Verification requirements (Rxx, for
outsourced review): [verification-spec.md](verification-spec.md). User-visible
workflows and wording: [WORKFLOWS.md](WORKFLOWS.md) and
[UI-TERMS.md](UI-TERMS.md). Visual language: [DESIGN.md](DESIGN.md).
Measurements and limits: [PERFORMANCE.md](PERFORMANCE.md).

Current version: **v1.0.230 (231)** · app id `com.keepmark.mobile` · display
name `Keepmark` · Android 10 (API 29) or later.

Distribution:

- Google Play (closed testing):
  <https://play.google.com/store/apps/details?id=com.keepmark.mobile>.
  Testers join <https://groups.google.com/g/keepmark-testers>, then opt in at
  <https://play.google.com/apps/testing/com.keepmark.mobile>.
- GitHub releases
  (<https://github.com/Keepmark/KeepmarkMobile/releases>, tag `v1.0.N`,
  `gh release create`): release notes only, **no APK or other assets** (since
  2026-10-15). Each release explains how to join the testers group, so new
  users come in through Play closed testing.
- The signed AAB for Play is built with the upload key
  (`~/keys/keepmark-upload.keystore`) by `scripts/build-play-aab.sh`.

`KeepmarkMobile/` is its own git repo (the parent `xBrowserSync/` directory is
not a git repo). Solution `KeepmarkMobile.slnx`:

| Project | Target | Role |
|---|---|---|
| `src/KeepmarkMobile` | `net10.0-android` | MAUI Blazor Hybrid host, Razor pages, platform services |
| `src/Keepmark.Core` | `net10.0` | UI-free logic: API, crypto, store, sync, safety, i18n, workflow |
| `tests/Keepmark.Core.Tests` | `net10.0` | xUnit suite (418 tests) |
| `tools/Keepmark.Benchmarks` | `net10.0` | store/sync performance + crypto interop probes |

## Contents

1. [Requirements and compatibility](#1-requirements-and-compatibility)
2. [Build environment](#2-build-environment)
3. [Commands](#3-commands)
4. [Architecture](#4-architecture)
5. [Local data and secrets](#5-local-data-and-secrets)
6. [Cryptography](#6-cryptography)
7. [API client and version negotiation](#7-api-client-and-version-negotiation)
8. [Sync engine](#8-sync-engine)
9. [Multiple saved syncs](#9-multiple-saved-syncs)
10. [Connect](#10-connect)
11. [Safety backups](#11-safety-backups)
12. [Encrypted backup export and import](#12-encrypted-backup-export-and-import)
13. [UI architecture and behavior](#13-ui-architecture-and-behavior)
14. [Internationalization](#14-internationalization)
15. [Tests and verification](#15-tests-and-verification)
16. [Release and distribution](#16-release-and-distribution)
17. [Engineering rules and gotchas](#17-engineering-rules-and-gotchas)
18. [License and notices](#18-license-and-notices)

---

## 1. Requirements and compatibility

### Product requirements

- **Android only**, API 29+.
- **Ten UI languages**: English (source), Italiano, Français, Español, Deutsch,
  日本語, Português (Brasil), Русский, 简体中文, Română (internal codes `ja`, `pt`, `ru`,
  `zh`, `ro`; `zh-TW`/`zh-HK` fall back to Simplified). Default follows the Android
  language; override in Settings → Language (the UI reloads without restart).
  Logs stay English. Every UI string is a `Str` key in
  `Keepmark.Core/I18n`; parity is enforced by `I18nTests`.
- **No telemetry**, no accounts, no analytics. The service sees ciphertext only.
- **Secrets stay out of logs**: no bodies, passwords, derived keys or full sync
  IDs (truncated by `LogSanitizer` / `WorkflowRules.ShortId`).
- **Nothing destructive happens silently**: conflict choices are explicit, and
  every overwrite or deletion snapshots the destroyed side first (§11).
- **One active sync at a time** per installation, with additional dormant syncs
  kept only as credentials (§9).

### Service and protocol compatibility

| Item | Value |
|---|---|
| Service API | any xBrowserSync-compatible service, min **1.1.9** |
| Default `Accept-Version` | **1.1.13** (satisfies the server `^1.1.3` route) |
| Optional extended contract | **1.2.0**, sent only after a capability probe |
| Sync format advertised | **1.8.0** (`SyncClientVersion`) |
| Remote sync format accepted | ≤ 1.8.0; newer is refused, never guessed |
| Crypto | PBKDF2-SHA256/250k + AES-GCM with 16-byte IV + lzutf8 |

Blobs are byte-compatible with the official xBrowserSync clients and with
Keepmark for browsers in both directions (see §6). The client is end-to-end
encrypted: service operators cannot read bookmarks.

### Service URL policy (HTTPS default, HTTP explicit)

- The curated catalog only contains HTTPS endpoints and HTTPS is the default.
- A **custom service may use plain `http://`** (typically a self-hosted
  instance on the home LAN). The custom editor is one row: scheme picker
  (`https` default) + host box. `WorkflowRules.SplitServiceUrl` /
  `ComposeServiceUrl` are inverses, and a host typed without a scheme is
  always composed as `https://`.
- Choosing `http` immediately shows the amber warning
  `Str.ServiceCleartext` in the editor (icon + sentence, `role="status"`): the
  sync ID (a bearer token) and the encrypted blob travel unprotected.
- Both schemes pass `ServiceSelector.Check`, `ConnectQrPayload.TryParse` and
  `ServiceCatalog.Remember`; no code compares scheme literals — always use
  `WorkflowRules.HttpScheme` / `PreferredServiceScheme`.
- Android network security: `release.xml` sets
  `cleartextTrafficPermitted="true"` globally, because Android's format can
  express host names, not address ranges. `debug.xml` allows cleartext only to
  `localhost` / `127.0.0.1` (for `adb reverse`), so a plain-HTTP LAN service is
  testable with a Release build, not with Debug.
- Privacy is still end-to-end: HTTP exposes the **sync ID** and traffic
  metadata, not bookmark contents. The UI says exactly that.

---

## 2. Build environment

```sh
export ANDROID_HOME=$HOME/android-sdk ANDROID_SDK_ROOT=$HOME/android-sdk
export JAVA_HOME=$HOME/jdk-21   # JDK 21; system JDK 26 is rejected (XA0030)
export PATH=$JAVA_HOME/bin:$HOME/android-sdk/platform-tools:$PATH
```

Device is WiFi-debug, IP:port rotates. Reconnect:

```sh
adb disconnect; adb connect <device-ip>:<port>; adb devices
# pairing port ≠ service port: `adb pair IP:PAIRPORT` (code), then `adb connect IP:SVCPORT`
# mDNS ghosts cause XA0010: `adb disconnect <ghost>` or `adb disconnect` (all), use explicit `-s IP:port`
# connection refused: the wireless-debug port rotated — find the open one with
#   nmap -p 30000-49999 -T4 --open <device-ip>
# with the "Wireless debugging" screen open, mDNS advertises a stable serial
# `adb-<id>._adb-tls-connect._tcp`; pass that to `-p:AdbTarget` too
# reach a local KeepmarkApi from the device (Debug allows loopback HTTP):
adb reverse tcp:8090 tcp:8090; curl -s localhost:8090/info   # then use http://localhost:8090 in-app
```

Linux prerequisites for the Core tests: Python 3.12+, GCC, make, Tcl,
pkg-config and OpenSSL development files. Run the SQLCipher preparation once
per checkout (§3). Workload problems (`MissingWorkloadPack`, workload-set
mismatch) are fixed with `sudo dotnet workload repair`; verify with
`dotnet workload list` and never paper over them with `DOTNETSDK_WORKLOAD_*`
overrides.

---

## 3. Commands

All commands run from `KeepmarkMobile/`.

```sh
python3 scripts/prepare-sqlcipher.py --linux   # once: Linux x64 native engine for tests
dotnet test                                    # Core unit + crypto/lifecycle regression tests
node tests/js/gestures.test.cjs                # gesture unit tests (node:test, no browser)

dotnet build src/KeepmarkMobile -f net10.0-android -c Debug
dotnet build src/KeepmarkMobile/KeepmarkMobile.csproj -t:Run -f net10.0-android -c Debug
# add "-p:AdbTarget=-s IP:PORT" when several adb entries are listed.
```

Deployment quirks:

- A Debug APK installed with plain `adb install` aborts ("No assemblies found …
  Fast Deployment"). Deploy Debug only through `-t:Run`.
- For a self-contained test APK, package Release:

```sh
dotnet build src/KeepmarkMobile -f net10.0-android -c Release \
  -t:SignAndroidPackage -p:AndroidKeyStore=false -p:AndroidPackageFormats=apk
# AndroidKeyStore=false uses the local debug key for testing only.
# Check the manifest version before installing; a plain build may leave an older APK.
```

Play upload (Release + upload key, outside the repo):

```sh
scripts/build-play-aab.sh          # asks the keystore password, builds, verifies
scripts/build-play-aab.sh --dry-run
# -> src/KeepmarkMobile/bin/Release/net10.0-android/com.keepmark.mobile-Signed.aab
#    (+ the same-name .apk for device QA), signed with ~/keys/keepmark-upload.keystore,
#    alias "keepmark". KEEPMARK_KEYSTORE / KEEPMARK_KEY_ALIAS override both.
# KEEPMARK_KEY_PASS or KEEPMARK_KEY_PASS_FILE (mode-600 file) skip the prompt.
```

Device inspection:

```sh
adb -s IP:PORT shell "dumpsys package com.keepmark.mobile | grep -m1 versionName"
adb -s IP:PORT exec-out screencap -p > /tmp/x.png     # screenshot
adb -s IP:PORT shell input tap 540 1200               # tap (device 1080x2412)
```

QA quirks: `input text` drops characters on WebView inputs — type one character
at a time (~0.3 s). A `screencap` taken right after the keyboard hides can be
corrupted; check `dumpsys input_method | grep mInputShown` before sending BACK
(BACK closes the keyboard first and only then navigates back) and re-capture.

Other scripts:

```sh
python3 scripts/verify-native-sqlcipher.py --apk /path/to/app.apk [--abi arm64-v8a]
python3 scripts/verify-sqlcipher-compat.py --old /path/to/old/libe_sqlcipher.so
dotnet run --project tools/Keepmark.Benchmarks -c Release
dotnet run --project tools/Keepmark.Benchmarks -c Release -- --interop /tmp/keepmark-csharp-blobs.json
node scripts/verify-csharp-blobs.cjs /tmp/keepmark-csharp-blobs.json
```

Version bump (manual): `ApplicationDisplayVersion` `1.0.N` and
`ApplicationVersion` `M` in `src/KeepmarkMobile/KeepmarkMobile.csproj`; a CSS/JS
change also bumps the `?v=` cache-buster in `wwwroot/index.html`. Commit message
format: `v1.0.N (M): short description`. **Docs-only changes do not bump.**
Commits happen in this repo; pushing to any remote requires an explicit
go-ahead in chat each time.

---

## 4. Architecture

### Process model

`MauiProgram` registers one instance of each service: `IXbsLocalizer`
(`XbsLocalizer`), `XbsSettings`, `LanguageService`, `SecureCredentialStore`,
`PinnedStore`, `ToastState`, `ISelectionFeedback`, `SharedImportService`,
`AppLockState`, `IQrScanner`, `XbsSession`, plus the Android
`IAppLockService` and `IStatusBarService`. `App.CreateWindow` wires
`Activated`/`Resumed`/`Stopped`/`Destroying` to `XbsSession.SetForeground`;
`App.OnStart` calls `XbsSession.InitAsync()` once (shared task) and then
initializes the lock state.

`XbsSession` is the app-scoped composition root at runtime: it owns the
SQLCipher store, the sync engine, the saved-sync registry, the safety subsystem
and the automatic poller. Components never talk to `SyncEngine` storage
directly for multi-step flows; they call session methods.

```
Razor page ──► XbsSession ──► SyncEngine ──► XSyncApiClient (per service URL)
                   │              │
                   │              └──► BookmarkStore (SQLCipher + FTS5)
                   ├──► SecureCredentialStore (KeyStore-backed SecureStorage)
                   ├──► SavedSyncRegistry (dormant sync rows)
                   └──► Safety backups / removed-sync copies (app-private files)
```

### Project layout

- `src/Keepmark.Core/Api/` — `XSyncApiClient`, version negotiation, response
  shapes, error mapping, retry policy, redacting logger.
- `src/Keepmark.Core/Crypto/` — `XCryptoService` (PBKDF2/AES-GCM/lzutf8),
  `BackupGuard`, `BlobHash`, bounds.
- `src/Keepmark.Core/Data/` — `BookmarkStore` (schema, FTS5, queries, bulk
  mutations), `SelectionBatch` (pure batch plan), `SqlCipherRuntime`.
- `src/Keepmark.Core/Sync/` — `SyncEngine`, `ForegroundPoller`,
  `SavedSyncs`/`SavedSyncRegistry` (pure registry + storage-facing registry),
  `BookmarkMerge` (pure union), `MoveResult` (result text), `SelectionBatch`.
- `src/Keepmark.Core/Safety/` — `SafetyBackups` (files, index, naming),
  `SafetyMaster` (wrapped master key), `RemovedSyncCopies` (shapes, hash, text)
  and `RemovedSyncCopyStore` (validate → encrypt → write → verify → list/read →
  delete pipeline, path guard, dedupe with relabel).
- `src/Keepmark.Core/Workflow/` — `ServiceCatalog`, `ServiceCheckState`,
  `ConnectQrPayload`/`ConnectQrCandidateState`, `WorkflowRules`,
  `WorkflowTexts`.
- `src/Keepmark.Core/I18n/` — `Str.g.cs`, ten `Catalog.*.cs`, `Localizer`,
  `RelativeTime`.
- `src/Keepmark.Core/Security/` — `LockPolicy` (relock presets).
- `src/KeepmarkMobile/Services/` — session, settings, secure store, language,
  app lock state, QR scanner, share intake, pinned store, selection controller.
- `src/KeepmarkMobile/Components/Pages/` — one page per workflow:

| Route | Page | Role |
|---|---|---|
| `/` | `Home` | tree, search, pinned/recent, sheets, share intake, selection |
| `/duplicates` | `Duplicates` | duplicate groups and bulk cleanup |
| `/backup` | `Backup` | encrypted export/import, safety backups, removed copies |
| `/syncs` | `Syncs` | saved syncs (activate, rename, forget, unlink, `Change password` on the active row) plus `+ Add a sync`, which opens the whole connect flow in place |
| `/connect` | `Connect` | redirect to `/syncs` (old links only) |
| `/settings` | `Settings` | theme, language, home sections, and the log panel folded behind one "Show logs" button (verbosity + current log inside) |
| `/settings/security` | `Security` | app lock and relock time |
| `/status` | `Status` | sync state, sizes, versions, removed-sync notice |
| `/help` | `Help` | user help |
| `/about` | `About` | versions, license, bug-report mail |
| `/switch-service`, `/migrate` | `MoveServiceLegacy` | redirect to `/syncs` (old links only) |

- `Components/` — shared UI: `ConnectPanel` (the add/connect flow used by
  `Syncs`), `BookmarkRow`, `BookmarkSheet`, `FolderPicker`,
  `MovePicker`, `DirectionPicker`, `ConfirmBlock`, `StepMessage`,
  `ServiceSelector`, `QrCredentialReview`, `SyncGoneNotice`, `RowTile`,
  `LogsPanel` (the log viewer + share export, embedded in `Settings`; the log
  is troubleshooting material, so it has no page and no menu entry).
- `Platforms/Android/` — `MainActivity` (non-edge-to-edge window, app lock on
  resume), `ShareActivity` (translucent SEND target), `ShareIntake`
  (publish/limbo/flush), `QrScanner` (ZXing.Net.Maui 0.10.4), biometric and
  status-bar services.
- `wwwroot/` — `app.css` (tokens, components, dark overrides), `js/gestures.js`,
  Figtree font (OFL).

### Data flow rules

- UI reads the store through `BookmarkStore`; mutations that must sync go
  through `SyncEngine` batch APIs so they are acknowledged and coalesced.
- `SyncEngine` is the only writer of sync metadata (`lastUpdated`, revision,
  hash, dirty) and the only component that pushes.
- `XbsSession` serializes the safety subsystem (`_safetyGate`) and the switch
  flows; it also owns the 401 notice state through the engine.
- Every service URL gets one cached `XSyncApiClient`, so `/info` capability
  probes are cached per URL and credentials never leak between services.

---

## 5. Local data and secrets

### Encrypted mirror (`xbs.db`)

`BookmarkStore` keeps a SQLCipher database at
`FileSystem.AppDataDirectory/xbs.db`, opened with a 64-hex device key. Startup
fails closed if `PRAGMA cipher_version` is empty: a mispackaged plain-SQLite
runtime must never silently store plaintext.

| Table | Columns / role |
|---|---|
| `Meta` | key/value: `lastUpdated`, `syncVersion`, `syncRevision`, `syncHash`, `dirty` (`"1"`/`"0"`) |
| `Blob` | `bookmarks` = last encrypted sync blob |
| `Items` | `RowId` (local, rekeyed on replace), `NodeId` (stable synced id), `ParentRowId`, `Title`, `Url`, `Description`, `Tags`, `DateAdded`, `IsFolder`, `SortOrder` |
| `ItemsFts` | FTS5 external-content index over title/url/description/tags, maintained by triggers |

- Tree structure is `ParentRowId` + `SortOrder`. `RowId` changes on every
  pull/restore; UI state (crumbs, expansions, pins, selection) must use
  `NodeId`.
- `ContentVersion` increments on content changes; components invalidate derived
  data against it.
- `Tags` are joined with U+001F. Descriptions are normalized on push (empty →
  null, max 300 chars).
- `ReplaceAllAsync`/`RestoreLocalAsync` replace the tree atomically and reset
  revision/hash for legacy pulls.
- `AcknowledgePushAsync` updates blob/meta and only changed normalized
  descriptions in place, preserving RowIds and FTS.
- Top-level `[xbs] Menu/Other/Toolbar` containers keep their synced `Title` but
  display a localized name (`ItemRow.DisplayTitle` → `ContainerNames`). Never
  write the localized name back.
- `DateAdded` is an optional Keepmark field (Unix ms, like the browser
  bookmarks API). It survives pull/push/merge/backup; new local nodes are
  stamped with "now" when missing. Legacy clients may drop it.

Indexes: `IX_Items_NodeId`, `IX_Items_Parent_Order`, `IX_Items_Url_Order`.

### SecureStorage (Android KeyStore-backed)

| Key | Content | Cleared by Unlink |
|---|---|---|
| `xbs_sync_id` | active sync ID | yes |
| `xbs_service_url` | active service URL | yes |
| `xbs_derived_key` | base64 derived key, never the password | yes |
| `xbs_sync_version` | active sync format version | yes |
| `xbs_db_key` | 64-hex database key, created once | **no** (kept forever) |
| `xbs_saved_syncs` | JSON array of dormant rows | no (active row is removed) |

`SecureCredentialStore` sanitizes the saved-sync JSON on read: unknown/broken
rows are dropped, URLs canonicalized, duplicates collapsed to the most recently
used. The raw sync password is never stored anywhere. Dormant rows keep the
derived key (same material as the active one), encrypted at rest by the platform
store.

### Preferences (`XbsSettings`)

Non-secret settings: `service_url`, `saved_services`, `saved_service_info`,
`log_verbosity`, `app_lock`, `lock_timeout_min`, `theme`,
`ui_language`, `open_mode`, `home_show_recent`, `home_recent_count`,
`home_recent_open`, `list_sort`.

The legacy `sync_mode` preference is ignored: mobile always uses automatic
synchronization while visible; Settings no longer offers a mode selector.

### App-private files

| Path | Content |
|---|---|
| `backups/xbs-backup-[<sync>-]YYYYMMDD-HHmmss.xbs.json` | user encrypted export (§12); `<sync>` is the active sync's name slugged, timestamp in device local time |
| `safety-backups/safety-*.json` + `safety-index.json` | safety snapshots (§11) |
| `safety-backups/safety-master.json` (+ `.prev.json` during rotation) | wrapped master key |
| `removed-syncs/removed-<sha256>.json` | copies of removed syncs (§9.5) |

### SQLCipher runtime

The ORM is `sqlite-net-base` 1.9.172 with
`SQLitePCLRaw.provider.sqlcipher` 3.0.5. `SqlCipherRuntime` initializes the
provider once; startup fails closed without `cipher_version`. Do not restore
`sqlite-net-pcl`, `bundle_green` or the deprecated `bundle_e_sqlcipher`.

Android uses the official SQLCipher Community Android 4.19.1 AAR runtime
(SQLCipher 4.19.0 / SQLite 3.53.4, arm64-v8a + x86_64). Binaries, SHA-256 pins,
source URLs and notices live in `native/sqlcipher/`. The unchanged
`classes.jar` is packaged with `AndroidLibrary Bind="false"` because
`JNI_OnLoad` requires the Java classes even though .NET uses only the C API;
`Xamarin.AndroidX.Sqlite` 2.6.2 supplies the Java interfaces. ProGuard rules
preserve the native registration roots; omitting them aborts startup with
`SQLiteCustomFunction` missing. Notices are bundled as an APK asset.

Linux tests and benchmarks build the same SQLCipher source release with system
OpenSSL via `scripts/prepare-sqlcipher.py --linux` (output in ignored
`artifacts/native/`). Android builds use the checked-in official binaries. No
database conversion, rekey or credential change is performed: the synthetic
2.1.10 fixture opens, searches, modifies and reopens under the new engine, and
unkeyed reads fail. `scripts/verify-sqlcipher-compat.py` proves new-write →
old-read/write → new-read including FTS. `scripts/verify-native-sqlcipher.py`
checks the pinned Java runtime, both ABI binaries, 16 KiB ELF/ZIP alignment,
uncompressed storage, and the absence of legacy/plain SQLite libraries.

---

## 6. Cryptography

### Sync envelope

```text
key    = PBKDF2-SHA256(UTF8(password), salt = UTF8(syncId), 250_000) → 256-bit
blob   = base64( IV(16 random bytes) || AES-128-GCM(lzutf8(JSON tree)) )
```

BouncyCastle is required because .NET `AesGcm` only accepts a 12-byte nonce.
The 16-byte IV is prepended, matching the original JS client.

- `XCryptoService.DeriveKey` / `Encrypt` / `Decrypt`.
- A sync without a version is refused (`UnsupportedSyncVersionException`,
  "open it once in an official xBrowserSync client"): the old crypto would only
  surface as a misleading wrong-password.
- Decryption failure surfaces as `InvalidCredentialsException`, never as a
  generic error.
- Sync format is checked before decrypting: remote > 1.8.0 is refused.

### Encrypted backup envelope

```json
{ "App": "xBrowserSyncNg", "Format": 1, "Salt": "<base64 16B>", "Blob": "<sync envelope>" }
```

PBKDF2-SHA256/250k with the random salt stored in the file, then AES-GCM +
lzutf8. The `App` marker stays `xBrowserSyncNg` so old files keep importing.

### Safety-backup envelope

Files are encrypted **once** under a random 256-bit Master Key, using the same
`XCryptoService` envelope. Only the master key is re-wrapped when the sync key
rotates. Removed-sync copies are encrypted with the device database key, which
survives an unlink.

### Limits (device resource limits, not server quotas)

| Limit | Value |
|---|---|
| Backup file | 50 MiB |
| Decoded encrypted envelope | 50 MiB |
| Decompressed UTF-8 | 256 MiB |
| Tree nodes (backup/selection validation) | 200,000 |
| HTTP JSON response | 72 MiB (base64 envelope + metadata) |
| HTTP error body | 64 KiB |
| New password | ≥ 8 characters |

Lzutf8 compression uses bounded hash chains (~384 KiB tables) and validates
output before allocating; base64 validation does not allocate decoded copies.
Extremely compressible payloads may be rejected even below the file cap.

### Interop discipline

Never change `Keepmark.Core/Crypto/` without running **both** directions:

1. Node vectors in `tests/Keepmark.Core.Tests/TestData/vectors.json`
   (generated with the original JS logic, `crypto.service.ts` + lzutf8@0.6.0).
2. `tools/Keepmark.Benchmarks -- --interop /tmp/keepmark-csharp-blobs.json`
   followed by `node scripts/verify-csharp-blobs.cjs /tmp/keepmark-csharp-blobs.json`
   (Node decrypts C# output with AES-GCM + lzutf8@0.6.0 for ASCII, Unicode,
   bookmarks and a larger payload).

---

## 7. API client and version negotiation

`XSyncApiClient` is created per normalized service URL by `XbsSession.ApiFor`
and calls:

| Call | Endpoint | Notes |
|---|---|---|
| `GetInfoAsync` | `GET /info` | legacy contract for the service check |
| `GetInfoV12Async` | `GET /info` with `Accept-Version: 1.2.0` | capability probe (revision, conditional GET) |
| `CreateSyncAsync` | `POST /bookmarks` | returns ID, version, `lastUpdated` |
| `GetBookmarksAsync` | `GET /bookmarks/{id}` | legacy full sync |
| `GetBookmarksV12Async` | same, v1.2 | includes `meta` |
| `GetBookmarksIfChangedAsync` | conditional `GET` with `If-None-Match: "<hash>"` | `304` → `null` |
| `GetLastUpdatedAsync` | `GET /bookmarks/{id}/lastUpdated` | cheap poll |
| `UpdateBookmarksAsync` | `PUT /bookmarks/{id}` | timestamp base |
| `UpdateBookmarksV12Async` | same, v1.2 | `baseRevision` CAS |

There is **no server-side sync DELETE** in this client (no `HttpMethod.Delete`).
The app cannot erase a server copy; `Unlink` only removes local credentials.

- `Accept-Version` is `1.1.13` by default. `1.2.0` is used only for the
  capability probe and, after a positive probe, for full GET / conditional GET /
  PUT with revision CAS.
- Timeouts: 12 s default, 3 s probe. Timeout / `HttpRequestException` →
  `ServiceOfflineException`. Reads retry with backoff (`Retry-After` honored);
  create and PUT never retry blindly (non-idempotent).
- Status mapping: 401 `SyncNotFound`, 404 `InvalidService`, 405
  `NotAcceptingNewSyncs`, 406 `DailyNewSyncLimit`, 409 `DataOutOfSync`, 413
  `SyncTooLarge`, 429 `Throttled` (+ `Retry-After`), 412/5xx/other mapped
  honestly. The server `code`/`message` shape is preserved for display.
- Bodies are never logged; sync IDs are truncated (`abcd…xy`); timings and byte
  counts are logged at Info.
- A v1.2 capability probe that returns `412`, lacks capabilities or fails stays
  on the legacy path. A service below 1.1.9 is refused instead of used
  silently.
- `capabilities.syncExpiryDays` is display-only on Status (`0` = kept forever);
  a malformed value must not break the probe.

---

## 8. Sync engine

`SyncEngine` owns the gate, the status and every read/write against the
service. It never resolves a conflict, never overwrites after a 409 and never
acts on a 401 without the user.

### Automatic sync and status

- Mobile always uses `SyncMode.Auto`: a foreground-only poller ticks 500 ms after the
  app becomes visible, then every 15 minutes **after the previous tick
  completes** (fixed delay, no catch-up storm).
- Local edits queue a coalesced push worker (250 ms grouping). **Sync now**
  remains available for an explicit attempt.
- `SyncMode.Manual` remains an internal Core API for tests, not a mobile setting.
- Statuses: `Idle`, `Syncing`, `Offline`, `Conflict`, `Error`, `NoCredentials`.
  `DataChanged` and `StatusChanged` events drive the UI; pages preserve scroll
  and query across events.
- `AutoSyncEnabled` follows visibility. Stopping cancels reads/retries and
  drains the worker; an already-sent PUT may finish and acknowledge its result.
  Dirty edits stay durable and are checked on resume.

### Whole-blob reconciliation

The API has no deltas: every sync compares `lastUpdated` first.

| Remote vs local | Local dirty | Action |
|---|---|---|
| remote ≠ local | yes | `Conflict` — never auto-merge |
| remote ≠ local | no | pull (full GET, or conditional 304) |
| remote = local | yes | push |
| remote = local | no | stamp `LastSyncAt`, `Idle` |

Additional rules:

- Pull refuses a dirty mirror (`DataOutOfSyncException`, status `Conflict`).
- Push encrypts the tree, normalizes descriptions, and uses revision CAS when a
  v1.2 revision is stored; otherwise the timestamp base. A 409 surfaces as
  `Conflict`; if the server already holds exactly our blob (ambiguous timeout),
  the client adopts the acknowledged state instead of reporting a phantom
  conflict.
- After a pull the engine stores the server version, revision and hash;
  `ReplaceAll` clears revision/hash for legacy pulls.
- Ordinary pushes preserve RowIds and FTS; an edit arriving during an upload is
  never lost, and redundant queued pushes skip clean data.
- `Normalize` on push: empty description/tags → null, description ≤ 300 chars.
- Version gates: service < `MinApiVersion` is refused; remote sync format >
  `SyncClientVersion` is refused on pull/compare/link.

### Conflict resolution

When status is `Conflict`, the UI offers download / upload / merge:

- `ResolveConflictPullAsync` — snapshot local → full GET → replace local.
- `ResolveConflictPushAsync` — fetch remote (for the snapshot and the push
  base) → snapshot remote → push local on that fresh base.
- `ResolveConflictMergeAsync` — fetch remote → snapshot both sides once → union
  → push; on 409 re-fetch and re-merge from the **original local tree**, max 3
  attempts.
- `GetConflictMergePreviewAsync` always re-fetches the remote and returns the
  same `BookmarkMerge.Merge` counts that apply will use.
- `RunConflictResolutionAsync` sets `_resolvingConflict`, suspends the auto
  timer, holds the gate for the whole resolution, and undoes both in one
  `finally` even when the gate wait times out.
- `SnapshotAsync` goes through `XbsSession.SaveSafetyBackupAsync`; if no
  callback is wired (unit tests) nothing is written, but production always
  wires it, and a real snapshot failure aborts the operation (fail closed).

Merge rules (pure `BookmarkMerge.Merge`, normative):

1. The remote is the shared truth: its nodes, ids and metadata are kept.
2. Local bookmarks whose normalized URL is absent remotely are adopted with
   fresh ids into the folder matched by full title path, or a new folder.
3. Same URL, different title → remote title wins (`TitleConflicts`).
4. Separators (`xbs:separator`) are unioned, never matched.
5. Deletes are not merged (no tombstones): items deleted on one side come back
   after a merge.
6. `NormalizeUrl`: lowercase host, drop default ports/fragments, remove
   tracking params (`utm_*`, `fbclid`, `gclid`, `msclkid`, `mc_cid`,
   `mc_eid`, `igshid`); path/query keep case. Unparsable input: trim,
   lowercase, drop fragment and trailing slash.
7. `remoteOnly` is computed against the remote as received; `localOnly` counts
   adopted local bookmarks at all levels.
8. A bookmark carrying children keeps the bookmark and flattens the children
   into the same folder.

### Gate and background discipline

- **Every `_gate.WaitAsync()` has a matching `_gate.Release()` in `finally`.**
- Conflict methods set `_resolvingConflict` in `try` and clear it in `finally`
  after the release; `ForceSyncAsync` and `PushInBackgroundAsync` check it
  after acquiring the gate and return early.
- Background timers never fire sync logic while `_resolvingConflict` is true.
- One 250 ms edit worker coalesces pushes; dirty/conflict/mode/visibility are
  re-checked under the gate.
- `XbsSession.InitAsync` shares one initialization task; components await it
  with their lifetime token and never subscribe after disposal.

### Push-base rule

Every PUT that follows an explicit GET (conflict resolutions, link upload/merge,
activation, password change) uses the `lastUpdated`/`meta.revision` of **that**
GET. The stored mirror base is exactly what caused the conflict, and after a
link it belongs to the previous sync.

### The 401 state (`GoneCredentials`)

A 401 means the service no longer knows the sync (expiry or deletion by another
client). The engine records `GoneSync(creds, dismissed)` and sets `Error`; it
does **not** unlink, retry, upload or snapshot. `GoneCredentials` is `null` once
the user dismisses it. Any successful sync (`Idle`) clears it; a 401 for a
different sync creates a fresh notice. See §9.5 for the UI and the copy flow.

---

## 9. Multiple saved syncs

### 9.1 Model

Exactly one sync is **active**: its credentials are the live
`SyncCredentials` in SecureStorage, and its tree is the only tree in `xbs.db`.
Every other saved sync is **dormant**: a row with service URL, sync ID, derived
key, optional label, optional sync version, key fingerprint and last-used time.
Dormant rows are never polled, pushed or decrypted except during an explicit
activation probe.

There is no schema change and no data migration: the active sync keeps using
the historical keys, and the registry is additive. On the first start after the
upgrade, `SavedSyncRegistry.SyncActiveAsync` registers the live credentials as
the first row (a no-op once it exists). That migration is best-effort: a
storage failure is logged and the app still starts.

### 9.2 Registry rules (`SavedSyncs`, `SavedSyncRegistry`)

- **Identity** = canonical service URL + sync ID (`SavedSyncs.Key`). The sync ID
  alone is not unique across services. `ServiceCatalog.Normalize` lowercases
  host/scheme and drops default ports/trailing slash while preserving path
  case.
- **Label** ≤ 40 characters, optional, unique case-insensitively; empty clears
  it. The display name is the label, or the service host when absent.
  `SuggestName` proposes the host, then "host 2", "host 3"… and is the rename
  field's placeholder. Uniqueness is checked against the **displayed** names,
  host fallback included, so no two rows can look identical.
- **Order**: last used first, then label, then sync ID (deterministic).
- **Upsert** refreshes `LastUsedAt` and preserves a label/version/fingerprint
  the caller did not supply.
- **Writes are serialized** (`SemaphoreSlim`): the list is read-modify-write, so
  two concurrent callers (startup migration + link, rename + activation) must
  not drop a row.
- **Deserialize** is defensive: malformed JSON → empty list; rows without service
  URL or sync ID dropped; labels normalized on read; duplicates collapsed to
  the most recently used.
- `KeyId` is `SafetyMaster.Fingerprint(derivedKey)` (first 8 hex of
  SHA-256), so the safety master can be pruned without holding keys.

### 9.3 Activate a dormant sync

Preconditions: the row exists and has a key, the sync is not already active,
and no other row operation is open.

1. **Inline confirmation first**: Activate opens the row’s `ConfirmBlock`.
   Only confirming it starts activation. With app lock on, the PIN/biometric prompt must
   succeed because the saved key alone is enough to switch. Without app lock
   there is no extra prompt beyond the inline confirmation.
2. **Read-only preflight** (`VerifySavedSyncAsync`): service `/info` version
   gate, full GET, decrypt with the row's stored key. A wrong/stale key, an
   expired sync or an unreachable service changes nothing. The sync's long
   password is never asked again.
3. **Fail-closed save of the active side** (`LinkAdoptRemoteAsync` →
   `PersistActiveSideCoreAsync`, under the same engine gate as adoption): pending conflict, offline
   service, vanished sync or failed push aborts the whole switch; the local tree
   is not replaced. Each cause keeps its own message (`ErrSavedConflict`,
   `ErrSavedOffline`, `ErrSavedGone`, `ErrSavedNotSaved`), so an offline device
   is never told to resolve a conflict it cannot resolve.
4. **Snapshot only when this device was unlinked** (`switch-take-remote`):
   switching from a linked sync replaces that sync's tree, which is already on
   its server (the switch is fail-closed on pending edits), so it needs no
   switch snapshot. Only an unlinked-device activation needs this snapshot;
   failure to write that required snapshot aborts adoption. Saving the old
   linked side may separately require a massive-change `sync-push` snapshot.
5. **Adopt** the target tree (`LinkAdoptRemoteAsync`: replace the mirror, then
   switch credentials). The probe already downloaded and decrypted the blob, so
   it is handed over as `PrefetchedRemote`: the same tree is never downloaded
   twice. If adoption fails, rollback restores the previous dirty flag as well
   as the tree and sync metadata, so pending edits remain eligible for upload.
6. **Rotate** the safety master from the old key to the new one (best effort,
   never fails the switch) and **remember** the target row (it becomes active).
7. The previously active sync stays in the list, dormant, ready to reactivate.

Failure surfaces stay next to the row (inline error), and a toast on success.
The list is refreshed in place.

### 9.4 Add a sync while linked

`Syncs → + Add a sync` is available only while linked and offers **only the
existing-sync path** (manual credentials or QR). Creating a brand-new sync is a
first-time flow: unlink first (bookmarks stay).

The flow reuses `ProbeLinkAsync` and the normal direction review:

| Direction | Behavior | Safety tags |
|---|---|---|
| Download | save active side (fail closed) → adopt remote → rotate → remember | none: the copy you leave is on the server |
| Upload | save active side → snapshot remote (the target's copy is replaced) → `LinkUploadAsync` on the probed base → rotate → remember | `upload-local` |
| Merge | save active side → union + push → rotate → remember | none: the union keeps both sides |

The added sync becomes active; the previous one stays in `Syncs`. A pending
conflict or failed push cancels the switch and changes nothing. Re-adding the
same canonical URL + sync ID updates the row in place, never duplicates it.
Note that only activation writes the `switch-take-remote` tag, and only when
it links an unlinked device; adding while linked is the same Connect apply
path and writes `upload-local` only when it replaces the target's server copy.

### 9.5 Forget, Unlink, and removed syncs

**Forget** (inactive row only): removes the row. Bookmarks, the server copy,
other rows and all backups stay. It then prunes that key's wrap from the safety
master when no saved sync (active included) still holds it
(`SafetyMaster.PruneWraps`), so dead wraps cannot crowd out a live key in the
max-3 list. The last wrap is never dropped; no backup is deleted
(`safety-master` keeps every file readable through any remaining wrap).

**Unlink this device** (active row): clears the four active credential keys,
removes the active row, clears the 401 state and puts the engine straight into
`NoCredentials` (the next poll tick would otherwise leave the UI showing the
linked state). The local tree stays, the server copy stays, dormant rows stay.
Safety wraps stay (deterministic PBKDF2 means re-linking the same sync+password
opens the backups again).

**Removed sync (401)** — the service no longer has the sync:

- Home and Status show one notice (`SyncGoneNotice`): `Try again`,
  `Unlink and keep a copy…`, `Cancel`.
- `Try again` clears the dismissal and runs an explicit `ForceSyncAsync`; a
  repeated 401 stays quiet (the notice text already explains it).
- `Cancel` dismisses that sync until an explicit retry or a different sync.
  Nothing is unlinked, uploaded or snapshotted automatically.
- `Unlink and keep a copy…` asks once, then:
  1. loads the local tree, validates it (`BackupGuard.ValidateTree`) and counts
     items (0 items → no copy, just unlink);
  2. hashes the serialized tree; the file name is `removed-<sha256>.json`
     (content-addressed, so identical trees are stored once);
  3. writes `{Format:1, CreatedAtUtc, ServiceUrl, Items, Tree}` encrypted with
     the device key (`RemovedSyncCopyStore`), then **verifies** by decrypting
     and re-hashing; a failed verification deletes the temp file and aborts
     before the unlink. An identical tree from another service rewrites the
     same file with the new `ServiceUrl` instead of adding a second one;
  4. unlinks the device (credentials + active row).
- Copies are listed in Backup → **Copies of removed syncs** (the section is
  hidden while there are none and nothing to report) with restore and
  delete (both confirmed), and are **never pruned automatically**. A damaged or
  foreign file is skipped with a warning, never listed and never deleted. File
  names are opaque to callers: only bare `removed-*.json` names inside the
  directory are accepted (no traversal, no absolute paths).
- Restore first snapshots the current local tree (`pre-restore`), replaces the
  local tree and marks it dirty; while linked, it is pushed at the next sync.
  Restore while linked is **fail-closed**: the active sync is saved first, and
  a pending conflict, offline service or unsaved push aborts with
  `ErrRestoreActivePending` before anything is replaced.
- If the copy list cannot be read, the section says so (`ErrLoadFailed`) and
  logs a warning instead of looking empty; the same applies to the safety
  backups list.

---

Remote adoption, upload and merge save the previous active side and replace
the mirror under one engine gate hold. Registry refresh, live tree capture and
any required safety snapshots run inside that same hold; queued edits cannot
interleave. New-sync upload also rechecks the active side inside this gate.
If remote adoption fails while switching credentials, its rollback restores the
tree, blob, timestamp, version, revision, hash and dirty flag together, keeping
pending edits eligible for upload.

## 10. Connect (the `+ Add a sync` flow inside Syncs)

### 10.1 States

There is no separate connect page: the flow is a component (`ConnectPanel`)
rendered **inside** `Syncs`, replacing the list while it is open. It owns no
list of its own and reports back with `OnFinished` (refresh the list) and
`OnCancel` (show the list again).

1. **Session not ready** — loading status.
2. **Panel closed** — the Syncs list (rows, active badge, actions) with
   `+ Add a sync` below it; while unlinked with no rows, the empty state with
   the same button.
3. **Mode choice** — only while unlinked: `Link an existing sync` and
   `Create a new sync`. While linked the panel opens directly on the
   existing-sync editor (the row that is active already has its own Unlink).
4. **Editor** — service selector, credentials, QR, verification, direction
   review and apply.
5. **Created** — the new sync ID in full, `Copia sync ID` and `Done`; the panel
   waits for that acknowledgement instead of dropping the only readable copy of
   the credential.

### 10.2 Service selection

`ServiceSelector` is used everywhere a service URL is edited:

- Eight curated public services, first (`api.xbrowsersync.org`) selected by
  default, then locally saved custom URLs, then "Custom URL…".
- Each row shows the host (marked default/saved) with country, size limit and
  API version. A service accepting existing syncs only is labelled; its
  message is shown with the endpoint.
- Only the selected service is probed. `Workflow/ServiceCheckState` ties
  readiness and create permission to a generation, so a late reply for an
  abandoned URL can never unlock the new one.
- Public/saved entries check automatically; a custom URL exposes a manual
  `Check service` and its proof is invalidated by any edit.
- `NoNewSyncs` blocks only "Create a new sync"; existing links still work.
- A custom URL is remembered (`XbsSettings.SavedServices`) only after a
  successful connect/create, never after a probe or preview; verified
  location/limit/version are refreshed only by later checks of that same URL.
  Saved rows stay selectable while temporarily offline.
- The selector opens on the service this device is linked to; the catalog
  default applies while unlinked.
- Everywhere a destination is named in words (review, confirmation, completion
  message) the full normalized URL is used, never the bare host.

**Status card** (`ServiceSelector.razor.css`): one card with a coloured dot and
a state word, endpoint chips (`API {version}`, `{size} MB`, translated
location), then the server note or the failure under a hairline. Precedence:
`checking` > `error` > `status`; pulsing grey while checking, green `Online`,
amber `NoNewSyncs`, red `Offline`/`Error`. Colour never carries the state alone.
It is `role="status"` + `aria-live="polite"`, and a failed check becomes
`role="alert"` with the message verbatim. Version/location chips disappear when
the service omits them; the size chip always shows. Country codes shipped by
the app are translated; anything else is printed as reported.

### 10.3 Create a new sync

The password is typed twice, minimum 8 characters (`WorkflowRules`). Two
variants:

- **Create empty** (`CreateNewSyncAsync`): save the active side (no-op when
  unlinked) → snapshot the local tree (`create-empty`) → `POST` → derive key
  from the returned ID → store credentials → replace the mirror with an empty
  tree → rotate master → remember the row. The local tree is replaced, hence
  the backup.
- **Create with upload** (`CreateNewSyncWithUploadAsync`): save the active side
  → `POST` → derive → `LinkUploadAsync` the current local tree on the creation
  timestamp → rotate → remember. Credentials switch only after the upload
  landed.

Both return the **full new sync ID**; the page shows it with `Copia sync ID`
and stores nothing but the derived key. The ID is a bearer token and cannot be
read again later.

### 10.4 Link an existing sync

1. Enter service, sync ID and password (or scan a QR, §10.5).
2. `ProbeLinkAsync` derives the key and runs `CompareAsync`: service version
   gate, full GET, decrypt, count both sides. Wrong password → zero mutations;
   legacy version-less sync → explicit refusal.
3. If both sides are empty, the link applies immediately with a `take-remote`
   snapshot.
4. Otherwise pick a direction in this order: `Keep the server copy` (Download),
   `Merge both`, `Keep this device's copy` (Upload). The only sensible direction
   is preselected when one side is empty; Merge is disabled unless both sides
   hold items.
5. Merge computes a preview with the pure union (same function as apply) and
   apply refuses without a current preview.
6. Apply uses the verified candidate, snapshots the destroyed side(s), pushes
   on the probed base, rotates the master and remembers the row. Success text
   restates the mode, direction and full target URL (`MoveResult.Describe`).

Verification hides the credential fields (the review is the confirmation);
changing direction or re-verifying invalidates the merge preview. Each action
cluster keeps its own adjacent error surface (`check` / `verify-link` /
`apply-review`, `role="alert"`), never one shared box.

### 10.5 Browser QR

Syncs → `+ Add a sync` → Existing sync (first link, and while linked) places a
QR icon and "Scan QR from browser" before the manual fields. `IQrScanner` /
`QrScanner` use pinned `ZXing.Net.Maui.Controls` 0.10.4.

- CAMERA is requested only on the scan action; camera hardware is optional; no
  photo, gallery, clipboard or image import. The scanner decodes QR only (UTF-8)
  and releases CameraManager/CameraX in every exit (back, cancel, modal close,
  app lock/background, page disposal).
- `ConnectQrPayload` validates before any network call: Keepmark JSON v1
  allowlist, 1,800 UTF-8 bytes, strict markers/types/duplicate names/depth,
  safe ID, supported sync version, absolute `https`/`http` URL without
  credentials, query, fragment, backslash or control chars.
- A valid scan produces an **in-memory candidate** only. `ConnectQrCandidateState`
  rejects stale/duplicate scans, expires against monotonic time at consumption
  and consumes the secret before probing. The review shows service URL and
  abbreviated ID, then `Continue` / `Cancel`; manual fields, service controls and
  later headings stay hidden while pending or verifying. `BeginVerification`
  keeps the review visible with "Verifying sync…" so no form flashes.
- Success enters exactly the manual `ProbeLink` → reconcile → apply path,
  including direction, preview lock and safety backups. Failure drops the QR
  password and requires a rescan; manual entry remains available.
- The candidate clears on consumption, failure, edits, cancel, background,
  lock or a 60-second local timeout. This is not credential expiry.
- **Known gap**: the cleartext warning is attached to the service editor, so a
  QR carrying `http://` shows the URL in the review but not the amber warning.

### 10.6 Unlink

`Unlink this device…` lives on the **active row** of `Syncs` (§9.5), not in the
add flow. It is armed inline (`Yes, unlink` / Cancel), clears the active
credentials and the active row, and keeps local bookmarks and the server copy.
The result says so. Undo is cleared.

### 10.7 Retired routes

`/connect`, `/switch-service` and `/migrate` all redirect to `/syncs`
(`Connect.razor`, `MoveServiceLegacy.razor`). They exist only so old links,
bookmarks and launcher shortcuts never hit a dead route. There is no multi-step
switch wizard, no resume draft and no "new sync next to an existing link" path.

---

## 11. Safety backups

### 11.1 Obligation

Every destructive operation snapshots the side it is about to destroy **before**
mutating anything, and tells the user where the snapshot went. No snapshot → the
operation must not run (fail closed, error surfaced).

Only steps that cannot be taken back cost a snapshot. A single edit, a move and
a move's undo are reversible in place (Undo, or the receipt's own restore), so
they write none: a snapshot there would only duplicate the tree the user still
has.

| Flow | Destroyed side | Snapshot | Operation tag |
|---|---|---|---|
| Connect → Download | local tree | local JSON | `take-remote` |
| Connect → Upload | server copy | remote JSON | `upload-local` |
| Connect → Merge | both sides | local + remote JSON | `connect-merge` |
| Connect → Create empty | local tree | local JSON | `create-empty` |
| Add while linked → Download | — | none (the copy you leave is on the server) | — |
| Add while linked → Upload | server copy | remote JSON | `upload-local` |
| Add while linked → Merge | — | none (the union keeps both sides) | — |
| Activate a saved sync (from unlinked) | local tree | local JSON | `switch-take-remote` |
| Activate from another linked sync | — | none (that copy is on the server) | — |
| Import encrypted backup | local tree | local JSON | `import-backup` |
| Change password | server copy | remote JSON | `change-password` |
| Duplicates bulk delete | local tree | pre-delete JSON | `duplicates-cleanup` |
| Selection → Delete | local tree | pre-delete JSON | `selection-delete` |
| Safety restore | current state | local or server state | `pre-restore` |
| Conflict → Download | local tree | local JSON | `conflict-pull` |
| Conflict → Upload | server copy | remote JSON | `conflict-push` |
| Conflict → Merge | both sides | local + remote JSON | `conflict-merge` |
| Sync download, more than 10 nodes changed | local tree | local JSON | `sync-pull` |
| Sync upload, more than 10 nodes changed | server copy | remote JSON | `sync-push` |

A normal sync takes no snapshot: replacing a whole tree is its normal shape.
When one side would lose more than `XbsConstants.MaxSilentSyncChanges` (10)
nodes something massive happened (a wipe, an import done outside the app,
another client gone wrong), so the side being replaced is snapshotted first
(`TreeMagnitude.ChangedNodeCount`: bookmarks by normalized URL, folders by path
below the three roots, counted per parent) and the sync continues; a failed
snapshot aborts it. Additions, removals and moves all count, and a move is a
removal plus an addition, so moving a folder with more than five bookmarks is
massive too.

Every push normalises the three containers (`ContainerNames.EnsureContainers`):
a tree missing Menu/Other/Toolbar would otherwise delete that container for
every other client on its next pull. The added roots are persisted in the
mirror at the same time, so the local and server shapes stay equal.

Exception: a single bookmark/folder delete on Home keeps its 6-second in-memory
undo instead of a file (documented; no snapshot required).

`switch-upload`, `switch-merge` and `switch-take-remote` remain known labels
for old backups; `switch-take-remote` is written only when activation links an
unlinked device.
`selection-move` and `selection-move-undo` are the same: older versions wrote
them, so `SafetyOperations.Describe` still names them; nothing writes them now.

### 11.2 Files, naming, manifest

- Payload: bookmark-tree JSON (same shape as sync blobs, plaintext) or, when an
  aligned link key exists, an AES-GCM envelope under the master key (§11.3).
  Extension always `.json`.
- Name: `safety-{yyyyMMdd-HHmmss}-{operation}.json` (UTC); second writes with
  the same operation get `-2`, `-3`, …
- Manifest `safety-index.json`: array of
  `{FileName, CreatedAtUtc, Operation, Side, Items, SizeBytes, SyncId, Enc}`.
  `SyncId` is truncated (`ab12…ef`); `Enc` is `"master"` or absent (plaintext).
- Legacy files (`safety-local-<ts>.json` / `safety-remote-<ts>.json`, no
  manifest entry) are still listed, restorable and deletable as "Older backup".
- Retention: newest N kept (Settings → "Safety backups to keep", `XbsSettings.SafetyKeep`, Preferences `safety_keep`, choices `SafetyBackupFiles.KeepChoices`, default 10); older files and index rows are pruned together, on every write and at once when the setting is lowered (`XbsSession.SetSafetyKeepAsync`, confirmed in the UI first).
  Manual delete is always allowed with two-tap confirmation.
- A corrupt index is treated as empty (the directory scan still lists files).
  A damaged payload refuses to open ("damaged"), never half-restores.
- Writes are atomic (temp + flush + rename); the prune set is computed before
  the index is rewritten and files are deleted only afterwards, so a crash can
  leave an orphan payload but never an index row pointing at a missing file.

### 11.3 Encryption and the Master Key

- Files are encrypted **once** under a random 256-bit Master Key, using
  `XCryptoService` (same envelope as sync crypto).
- `safety-master.json` holds only wrapped copies:
  `{Format:1, Wraps:[{KekId, WrappedKey}]}`, newest first, max **3**.
  `KekId` = first 8 hex of SHA-256(KEK); KEK = current sync-derived key.
  The plaintext master never touches disk.
- Pre-link/misaligned writes fall back to plaintext flagged `Enc: absent`
  ("not encrypted" in the UI) — never silently, never assumed encrypted.
- Rotation (link, activation, add, password change): unwrap with the old KEK,
  wrap with the new KEK, write temp + fsync + atomic rename, then test-unwrap
  before trusting; the previous file is kept as `.prev` until verification
  passes. A rotation failure never fails the user flow (warn + continue).
- Unlink drops the key from memory, but deterministic PBKDF2 means re-linking
  the same sync + password re-opens every backup without a recovery flow.
- Forgetting a dormant row prunes that key's wrap when no saved sync still
  holds it; the last wrap is never dropped. Backups are never deleted by
  pruning.
- Concurrent access is serialized by the session safety gate; misaligned states
  refuse honestly ("locked to another sync (…)") instead of guessing.

### 11.4 Restore semantics

- **Local-side entry**: snapshot the current local tree (`pre-restore`), then
  replace the local tree. It works unlinked; the mirror is dirty and is pushed
  at the next sync (or surfaces an honest conflict).
- **Remote-side entry**: requires an active link (otherwise refuse: "Link the
  sync this backup belongs to first"), snapshots the **real** server copy,
  replaces the local tree and pushes under the current key with revision CAS.
  A 409 surfaces; nothing is clobbered.
- **Restorable here** (`SafetyBackupFiles.Lock`): the master key opens every
  encrypted file, so decryption cannot tell syncs apart. Linked, an entry whose
  truncated `SyncId` differs from the active sync's is `OtherSync` (restoring it
  would push that sync's bookmarks into this one); unlinked, an encrypted or
  remote-side entry is `NeedsLink`. Backup hides locked rows behind a
  "Restorable here / All" filter (shown only when some exist), dims them with
  the reason, keeps Delete and offers `Activate <name>…` (→ Syncs) when
  `SafetyBackupFiles.Owner` finds one inactive saved row.
  `RestoreSafetyBackupAsync` refuses both states as a backstop.
- The used entry is never deleted. Counts shown before confirm come from
  `ValidateTree` (bounded tree, no nulls).

### 11.5 UI obligations

- Every destructive success message names the safety net and where to find it
  ("… — see Backup → Safety backups").
- Each safety row shows local time, operation description, side (local tree /
  server copy), item count, size, origin sync and encrypted flag.
- Restore and Delete require explicit confirmation; errors show the reason
  verbatim; consumed secrets are cleared on failure.
- Change password and link-existing keep their verify-first gates; nothing in
  this spec weakens them.
- Multi-outcome successes restate WHAT ran (mode + direction + target) through
  `MoveResult.Describe`, never a bare "Done".
- Merge is preview-locked: changing direction or re-verifying invalidates the
  preview, and apply refuses without a fresh preview for the current probe.

### 11.6 Third-party verification checklist

1. `dotnet test` green (safety suite: naming, describe, prune-10, legacy parse,
   index round-trip/damage, fingerprint, wrap trim).
2. Trigger each flow in §11.1 → Backup → Safety backups shows a row with the
   right operation/side/count; the filename matches on disk.
3. Restore a local entry → tree replaced, `pre-restore` row appears, count
   matches.
4. Restore a remote entry while unlinked → refused with the link message.
5. Delete a row → file + index row gone; restore-then-delete of the same name →
   "not found".
6. Activate another sync / change password → previous rows still open
   (re-wrapped, origin sync shown).
7. Unlink → encrypted rows refuse with the origin sync named; re-link the same
   credentials → they open again.
8. Kill the app mid-rotation (or restore `.prev` over master) → master intact or
   verifiable, no silent lockout; index damage → list falls back to the
   directory scan.
9. Merge in Connect: preview counts equal the applied result; TWO new rows
   (`connect-merge`, local + remote) with the right counts; the tree holds the
   union and nothing is deleted. Old `switch-merge` rows stay readable.
10. Merge preview lock: preview, change direction and back (or re-verify) →
    apply refuses with "Preview the merge first — …".
11. Merge with a wrong password → `InvalidCredentials`, zero mutations, zero
    safety rows.

---

## 12. Encrypted backup export and import

- **Export** (`ExportBackupAsync`): validates the password (≥ 8, typed twice in
  the UI), loads the mirror, validates the tree, derives a key from a random
  16-byte salt, writes the envelope to
  `backups/xbs-backup-[<sync>-]YYYYMMDD-HHmmss.xbs.json`, and offers the system
  share / folder UI. `<sync>` is the active sync's name (label, else service
  host) slugged by `BackupGuard.Slug`: lowercase, accents folded, anything that
  is not a letter or digit collapsed into one underscore, at most 40 characters;
  without a usable name the file keeps the plain timestamped name. The backup password is independent of the sync password and cannot
  be recovered.
- **Preview** (`PreviewBackupAsync`): checks the file size, deserializes from a
  stream, validates the marker/salt/blob, distinguishes damaged file from wrong
  password with a base64 pre-check, decrypts, validates the tree and returns the
  item count. Nothing changes.
- **Import** (`ImportBackupAsync`): re-opens the file, snapshots the current
  local tree (`import-backup`), replaces the local tree (dirty). While linked it
  is pushed at the next sync; a 409 surfaces honestly. The review is the only
  confirmation and its final button executes immediately.
- A wrong password clears only the password; the chosen file stays.
- File suffix is `.json` (export name `.xbs.json`); limits are in §6.

---

## 13. UI architecture and behavior

### Pages and shared components

`SessionComponentBase` waits for `XbsSession.InitAsync` with the component
lifetime token, exposes `L`/`Loc` and `Lifetime`, and prevents work after
disposal. Shared components are presentation-only: data in through
`[Parameter]`, events out through `EventCallback`; no direct store fetches.

- `BookmarkRow` renders a bookmark/folder/separator row with ribbon, folder
  glyph, swipe-expanded details and action affordances.
- `BookmarkSheet` is the add/edit bottom sheet (snapshot anti-wipe, dirty
  discard confirm, validation, http/https absolute URL).
- `FolderPicker`/`MovePicker` render the collapsible tree with explicit
  Select/Cancel; MovePicker adds recent destinations and inline folder creation.
- `DirectionPicker` implements the fixed direction order and consequence text.
- `ConfirmBlock` is the single inline confirmation block (armed boolean, action
  + Cancel, busy state). In `Syncs.razor` a row task (confirmation or rename
  editor) disables every other row and the `+ Add a sync` button, and the
  owning row is outlined: one operation at a time, on its own surface.
- `StepMessage` renders one success/error line next to its action cluster.
- `SyncGoneNotice` is the 401 notice used by Home and Status.

### Async and rendering patterns

- **Optimistic delete**: remove the row from local lists + `StateHasChanged()` +
  `Task.Delay(1)` first, then DB/sync work, then `ReloadAsync()`. The list must
  react instantly.
- **Busy before toast**: set `busy=false` and `StateHasChanged()` BEFORE
  `Toast.ShowAsync` (the toast lasts ~3.5 s; buttons must re-enable at once).
- Toasts are fire-and-forget on the UI path; never await one while list state is
  still unpainted.
- Fire-and-forget JS no-ops: `_ = JS.InvokeVoidAsync(...)`, never awaited before
  UI work.
- Modal sheets lock page scroll via `xbsGestures.lockScroll(bool, owner)`
  (`"sheet"`, `"nav"`); the page stays locked while any owner asks. Backdrop
  `touch-action: none`, sheet `touch-action: pan-y`, both
  `overscroll-behavior: contain`. Home syncs the lock after render and releases
  it in `Dispose`.
- `Window.Activated/Resumed/Stopped/Destroying` control visibility; polling and
  the relative-time clock stop in the background. Global folder/count/pin data
  refresh only after `ContentVersion`, language, pins or relevant settings
  change.
- Uniform collapsed folder lists above 100 rows virtualize; duplicate groups
  page in batches of 100; expanded/separator rows use the normal renderer.
  Explicit location jumps render the full target folder until the next
  navigation to keep scroll anchors.
- Gesture registrations are owner-keyed and released with their component;
  layout measurements are coalesced into one animation frame.

### Gestures and selection

- Delegated listeners on `document`: tap opens, swipe right expands, swipe left
  collapses, long press (500 ms) opens the action sheet or, since v1.0.201,
  starts multi-selection with native haptics.
- Swipe thresholds: 12 px with a 1.5× ratio to start, 60 px to confirm; a
  swipe/long press suppresses the following tap (400 ms). Separators and inner
  buttons are ignored.
- `Home.Selection.cs` coordinates `SelectionController`, the store
  `SelectionBatch` and gated engine batch APIs. IDs are resolved and validated
  inside the same transaction as the mutation; one engine notification follows
  the batch.
- Selection captures `NodeId` + content version; a content change invalidates
  it. All/None applies to loaded logical candidates (including virtualized rows,
  not unseen search results). Folder subtrees are included and overlapping
  search descendants are covered once. Top-level system containers and
  separators cannot be selected (informational toast, no persistent error).
- Move appends roots in captured display order and skips roots already in the
  destination. No-op batches create no backup and do not notify sync. Undo lasts
  six seconds and restores only the batch positions; any intervening content
  change refuses the whole undo. Delete has one confirmation with separate
  root/contained counts and recovery through safety backups, not a global undo.
- Single and batch moves share the six-entry recent-destination history (stable
  NodeIds, most recent first). A new folder is saved immediately in the chosen
  path and selected; cancelling the picker leaves it. Move stays disabled while
  the folder name is a draft.
- Move/delete snapshot the local tree under the engine gate and read-verify the
  backup before mutation. Backup failure leaves the batch untouched. A
  successful local write never claims the network is synchronized.
- Selected folder subtrees are excluded from destinations. New-folder creation
  is gated against the expected version and returns the new version explicitly.

### Home structure

- Fixed top bar with brand + hamburger (drawer overlays; it never becomes a
  sidebar on tablets).
- Sticky stack: search → breadcrumbs → sort, each with the body background and
  decreasing z-index so rows never show through.
- Pinned and Recently added cards keep their ribbon heads when open and a muted
  preview line when closed. Recently added = newest `dateAdded`, undated after
  them by `NodeId`, with folder path; Settings controls on/off, 5/10/20 and the
  open state. "See all recent" is a virtual crumb level (up to 50).
- Floating Add FAB and a fixed status bar; sync progress uses an indeterminate
  top loader + dark `Syncing…` pill.
- Folders first; display sort (Default/Asc/Desc) is UI-only and never written
  to the sync blob.
- `DisplayTitle` localizes top-level `[xbs]` containers; edit forms show the raw
  synced title.

### Accessibility and error surfaces

- Focus moves to the `h1` on navigation (`FocusOnNavigate`).
- Dialogs are `role="dialog" aria-modal="true"`; trees use `role="tree"` /
  `treeitem` with `aria-selected`, `aria-level`, `aria-expanded` only where
  expand/collapse is real; list rows are `role="button" tabindex="0"` and only
  Enter activates (Space scrolls).
- Errors are `role="alert"`, counts/states `role="status"`, sync
  `role="progressbar"`; `aria-invalid`/`aria-describedby` on fields.
- Touch targets ≥ 44 px (48 px for rows/search/crumbs/pinned header); visible
  tap/selected/active feedback.
- Status and colour never alone: every state has a word or icon.
- Each action cluster owns its adjacent error surface; a failed service check
  turns the status card red with `role="alert"` and the verbatim message.

### Razor/Blazor gotchas

- String component parameters without `@` pass literals.
- A ternary with `: null` or nested ternaries inside attributes fails to parse —
  use a `@code` helper.
- Blazor renders bool ARIA as `True`/`False`: lowercase explicitly.
- Inside `@if { }` whitespace between/around inline expressions and `<text>` can
  be dropped (seen live as `2 KBof 1 MB`): build one string
  (`@($" {a} {b}")`) or emit `@(" ")`.
- URL inputs need `autocapitalize="off" autocorrect="off" spellcheck="false"`
  (Android capitalized `Https://`); store through
  `WorkflowRules.NormalizeServiceUrl`.
- sqlite-net enables shared cache; SQLCipher `PRAGMA key` needs
  `ExecuteScalar`.
- AndroidX Biometric forbids a custom negative button with
  `DEVICE_CREDENTIAL`.

### Visual rules

Design tokens, layout, components, z-index scale and the accessibility checklist
are normative in [DESIGN.md](DESIGN.md). Highlights: never cover the status
bar/notch (non-edge-to-edge window + safe-area + confined scroll), `--km-*`
tokens for brand colours, `xbs-*` classes for custom behavior, `km-*` scoped
classes for local presentation only, status bar 1070 < sheet 1100 < toast 1200 <
lock 2000.

---

## 14. Internationalization

- Every UI text is a key in `src/Keepmark.Core/I18n/Str.g.cs`
  (`public const string`) and a value in **all ten** `Catalog.*.cs` files.
  `I18nTests` enforces key parity and that every `Str` constant exists in
  English.
- `Loc.Get(key, args)` uses `Langs.Current` (explicit setting, else
  `CurrentUICulture`, else `en`) and falls back to English, then the key.
- Plurals: `baseKey_one` / `baseKey_other`; French and Brazilian Portuguese use
  `_one` for 0; Japanese and Chinese use the same text for both; Romanian and
  Russian `_other` is the plain plural (`Intl.PluralRules` maps Romanian `few`
  there), Russian written count-neutral ("Закладок: {count}") so 2–4 and 5+ read
  correctly.
- `LanguageService.Apply` sets culture and the process-wide `Langs.Forced`,
  bumps `Version` and raises `Changed`; the layout re-renders on that version so
  a language change needs no restart. If the language matches the system
  culture, the system region (date/number formats) is kept.
- Relative times: `RelativeTime.Format` (just now / N min / N hours same day /
  yesterday / N days / N weeks / date) in the device time zone; the action sheet
  shows `Added: <date time> · <relative>` and drops the relative part after 30
  days.
- Log strings and exception diagnostics stay English. UI terms and translations
  are normative in [UI-TERMS.md](UI-TERMS.md); workflows in
  [WORKFLOWS.md](WORKFLOWS.md).

---

## 15. Tests and verification

### Automated suites (418 tests, no device)

| Suite | Proves |
|---|---|
| `ApiClientTests` | routes, shapes, version headers, error mapping, timeouts, expiry capability |
| `CryptoInteropTests` + `vectors.json` | sync envelope both directions |
| `BackupGuardTests` | backup limits, tree validation, password rules |
| `RetryPolicyTests` | read retries/backoff, no blind create/PUT retry |
| `StoreSyncTests` | mirror/meta/dirty, pull/push, stable RowIds, restore |
| `BookmarkMergeTests` | union rules, ids, folder paths, empty sides, URL identity, flatten, `MoveResult` |
| `ConflictResolutionTests` | fake server with real base semantics, 409 handling, merge retry |
| `SafetyBackupTests` | naming, describe, prune-10, legacy parse, index round-trip/damage, fingerprint, wrap trim |
| `SavedSyncsTests` | identity, canonical URL, labels (host fallback), upsert, sort, serialized concurrent writes, defensive JSON |
| `SavedSyncActivationTests` | read-only activation probe, fail-closed switch, previous row kept |
| `RemovedSyncNoticeTests` | 401 notice state, dismiss/retry, copy-before-unlink, dedupe |
| `RemovedSyncCopyStoreTests` | copy pipeline: validation, encrypt/verify, dedupe + relabel, damaged/foreign files, path guard, wrong device key |
| `ServiceCatalogTests` | catalog, normalization, saved metadata, stale checks |
| `ConnectQrTests` | strict parser, bounded sizes, candidate generations/timeout/consume |
| `WorkflowTests` | URL split/compose, password rules, short IDs, cleartext detection |
| `I18nTests` | ten-language parity and `Str` coverage |
| `SelectionBatchTests`, `SelectionControllerTests` | batch validation, undo, version gates |
| `ForegroundPollerTests`, `LockPolicyTests` | lifecycle/cancellation, relock presets |
| `VersionCompareTests` | numeric dotted compare |
| `StorePerformanceTests`, `SyncPerformanceTests` | query plans, targeted paths, aggregation, allocations |

Commands: `dotnet test` (after `scripts/prepare-sqlcipher.py --linux` once) and
`node tests/js/gestures.test.cjs`. Performance and interop commands are in §3
and PERFORMANCE.md.

### Manual / device verification

The per-view checklist is in
[verification-spec.md](verification-spec.md) §11. No view may be skipped: Home,
Duplicates, Backup, Syncs, Connect, MoveServiceLegacy, Settings, Status,
About, plus cross-view checks (live v1.2 conditional GET/CAS, restore against a
real server, uninstall/reinstall data loss). QR hardware cases (permission
denied/revoked, rotation, biometric lock, camera indicator off) and TalkBack,
font scaling and haptics require a real device.

### Release checklist

See §16 and verification-spec.md §13.5.

---

## 16. Release and distribution

1. `dotnet test` green.
2. `dotnet build src/KeepmarkMobile -f net10.0-android -c Debug` with 0 errors.
3. Device deploy via `-t:Run`; walk the changed flows; check
   `dumpsys package … versionName`.
4. Bump `ApplicationDisplayVersion`/`ApplicationVersion`; bump `?v=` in
   `wwwroot/index.html` when CSS/JS changed.
5. Commit as `v1.0.N (M): …` (no AI attribution), then ask before pushing.
6. Play: `scripts/build-play-aab.sh`, verify the AAB with
   `keytool -printcert -jarfile`, upload to closed testing.
7. GitHub release: `gh release create v1.0.N` with notes only (no APK/assets),
   explaining how to join the testers group.

Docs-only changes do not bump the version and do not need a device build.

---

## 17. Engineering rules and gotchas

### Non-negotiable

- **Efficiency**: batch work into the fewest transactions and network calls.
  Never loop single-item operations when a bulk path exists — one sync storm can
  get the service to throttle or blacklist us. Example: duplicates cleanup
  deletes everything in ONE store transaction + ONE push.
- **Safety**: destructive work resolves by stable identity (`NodeId`, never a
  possibly-rekeyed `RowId`), validates before acting, keeps an undo or
  safety-backup path, and logs failures with context instead of swallowing them.
- **Atomicity**: multi-step mutations go in one SQLite transaction where
  possible (resolve + delete inside one `tran`), so a concurrent push/pull
  rekey cannot strand half a batch.
- **Fail closed**: no safety snapshot → no destructive operation; failed
  verification → no switch; pending conflict → no activation.
- **Secrets**: raw passwords only as method arguments; never persisted, never
  logged, never in filenames or toasts. Sync IDs are bearer tokens: truncate
  them in UI and logs.
- **No silent conflict resolution**: a 409 is always surfaced; merge retries
  re-fetch and re-merge, max 3 attempts.

### Database and platform gotchas

- Never edit `bin/`/`obj/` output or generated `Str.g.cs` by hand (regenerate
  keys intentionally and keep catalogs in sync).
- Do not restore plain SQLite packages; startup must keep failing closed when
  `cipher_version` is absent.
- Keep the `safety-*` filename/manifest formats stable: old backups must stay
  listable and restorable.
- Keep `SavedSyncs.Deserialize` defensive: it runs inside the link flow and must
  never throw on old or hand-edited JSON.
- The app is not edge-to-edge; do not enable it (status bar must stay visible).
- SQLite `RowId` is not identity; always rebase UI state on `NodeId` before
  redrawing after a sync.

### Current known gaps

- The QR review does not repeat the plain-HTTP warning (§10.5).
- The 401 notice's explanatory text names the common three-week expiry; a v1.2
  service may advertise a different `syncExpiryDays`, which Status shows.

---

## 18. License and notices

Proprietary — © 2026 Yari Melzani, all rights reserved. See
[`LICENSE.md`](../LICENSE.md). Independent implementation, compatible with the
xBrowserSync protocol; not affiliated with or endorsed by xBrowserSync.

Third-party notices: `Crypto/LzUtf8.cs` is a C# port of `lzutf8` (MIT, © Rotem
Dan); BouncyCastle (MIT); SQLCipher Community (BSD-style), SQLite (public
domain), LibTomCrypt (public-domain option); Figtree (OFL). See
[`native/sqlcipher/THIRD-PARTY-NOTICES.txt`](../native/sqlcipher/THIRD-PARTY-NOTICES.txt),
also bundled in the app.

Saved-sync rows use a prominent Activate button, disabled on the active row.
Secondary actions open from an accessible ⋮ menu: Rename and active-row Change
password first; Forget/Unlink in a separate final group. Settings places the
complete log controls last; every setting applies on change (no Save). See WORKFLOWS.md for the
shared presentation and confirmation contract.
