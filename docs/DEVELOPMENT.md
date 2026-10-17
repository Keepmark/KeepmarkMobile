# Keepmark for Android — developer guide

Technical reference for contributors and reviewers: requirements, build
environment, architecture, engineering rules and the safety-backup spec.
Product overview: [README](../README.md).

> Behavioral spec for outsourced code verification:
> [`docs/verification-spec.md`](verification-spec.md) (requirements Rxx —
> read it before reviewing code).

Talks to any xBrowserSync API **v1.1.13** service (min v1.1.9), plus the
v1.2 extended contract when advertised. Current version: **v1.0.208 (209)**,
app id `com.keepmark.mobile`, display name `Keepmark`.

Distribution:

- Google Play (closed testing):
  <https://play.google.com/store/apps/details?id=com.keepmark.mobile>.
  Testers join <https://groups.google.com/g/keepmark-testers>, then opt in at
  <https://play.google.com/apps/testing/com.keepmark.mobile>.
- GitHub releases (<https://github.com/Keepmark/KeepmarkMobile/releases>,
  tag `v1.0.N`, `gh release create`): release notes only, **no APK or other
  assets** (since 2026-10-15). Each release explains how to join the testers
  group, so new users come in through Play closed testing. The signed AAB
  for Play is built with the upload key (`~/keys/keepmark-upload.keystore`).

`KeepmarkMobile/` is its own git repo (root `$HOME/Projects/xBrowserSync`
is NOT a git repo). Solution `KeepmarkMobile.slnx`:
`src/KeepmarkMobile` (MAUI Blazor, `net10.0-android` only, min API 29),
`src/Keepmark.Core` (net10.0, UI-free logic),
`tests/Keepmark.Core.Tests` (xUnit).

## Requirements (load-bearing)

- Android only, API 29+. UI in en/it/fr/es/de/ja/pt-BR/ru/zh (codes `ja`, `pt`, `ru`, `zh`; zh-TW falls back to Simplified): default follows the Android language, override in Settings → Language (reloads the UI). Logs stay English. Every UI text is a key in `Keepmark.Core/I18n` (`Str` + `Catalog.*.cs`, parity checked by `I18nTests`); wording follows `docs/UI-TERMS.md`.
- Top-level `[xbs] Menu/Other/Toolbar` containers are shown with localized
  names (`ItemRow.DisplayTitle` → `ContainerNames`, v1.0.170). Display only:
  `Title` keeps the synced value every client looks containers up by, and the
  edit form still shows it raw. Use `DisplayTitle` for any new title/path in the UI.
- Status and Switch service show the server's inactive-sync expiry from the
  v1.2 `/info` `capabilities.syncExpiryDays` (`0` = kept forever; v1.0.171):
  `XbsSession.GetSyncExpiryDaysAsync` → `WorkflowRules.DescribeSyncExpiry`.
  Display only, never stored; the line is hidden on legacy/offline services.
  `ApiCapabilities.SyncExpiryDays` reads a raw `JsonElement`, so a malformed
  value is ignored instead of failing the v1.2 probe.
- Sync: auto default + manual mode; **409 conflict never silently merged**;
  safety backup before overwrites; display sort never affects sync order;
  background push serializes with pull on the engine gate.
- Home sections (v1.0.198, canvas "Filo di colore"): Pinned and Recently added
  keep their hanging ribbon heads (amber / forest green), also when open.
  Open cards have a 3px side accent (opacity 0% → 100% at midpoint → 0%), starting exactly at the
  perforated divider (grid body row, following the real header height), without
  decorations, plus a visible neutral outline. Rows and "See all recent" use the full card width;
  the previous 44px solid ticket gutter is removed. Light and
  dark themes have separate neutral panel/border colors. Closed cards retain
  their original layout: a muted line under the label previews them (canvas
  "F2"): Pinned lists its titles, Recently added shows
  the newest title + its relative time. Recently added = newest bookmarks by `dateAdded`,
  undated ones after them by `NodeId` (`BookmarkStore.GetRecentBookmarksAsync`;
  ids are MAX+1 on add), with
  folder path; Settings → Home toggles it and sets 5/10/20; "See all recent"
  is a virtual crumb level (up to 50).
- `dateAdded` (v1.0.181): optional Keepmark field on synced nodes, Unix ms
  like the browser bookmarks API, omitted when null. Stored in
  `ItemRow.DateAdded`, kept through pull/push/merge/backup; nodes added on the
  device are stamped with now (`StampMissingDates`, an existing value wins).
  Legacy xBrowserSync clients may drop it: rows without it show no time.
  Recent rows show `RelativeTime.Format` after the folder path (just now /
  N min / N hours same day / yesterday / N days / N weeks / date, calendar
  steps in the device time zone); the action sheet shows "Added: <date time>
  · <relative>" (`RelativeTime.Detail`, relative part dropped from 30 days).
  Home re-renders them every minute while visible.
- UX: tap opens; long press enters context-local multi-selection with native haptics; ⋮ keeps single-item actions plus Select; swipe right expands details /
  left collapses, pinned section, clickable breadcrumb, folder-first sorting,
  share-receiving with prefilled form (incoming shares close single-item action,
  move and delete panels and replace any open edit form; selection/batch flows defer intake), full-screen folder tree picker with
  explicit Select/Cancel.
- Top bar: single fixed bar with brand mark left + hamburger right (fixed, not
  sticky: the `.sidebar` parent is only as tall as the bar, so sticky had zero
  room and scrolled away);
- Renamed v1.0.116: project/namespace/folder `src/Keepmark` → `src/KeepmarkMobile`
  (`KeepmarkMobile.slnx`, `KeepmarkMobile.csproj`, root namespace `KeepmarkMobile`;
  the library stays `Keepmark.Core`, aliased as `Core` in `GlobalUsings.cs`), app id
  `com.keepmark.legacy.keepmark` → `com.keepmark.mobile` (= fresh install, re-link or
  backup-import; the old app stays installed until removed by hand).
- Renamed v1.0.76: app/namespace/folders `XBrowserSyncNg` → `Keepmark`, app id
  `com.keepmark.legacy.keepmark` (= fresh install, old app data stays in the old app —
  re-link or backup-import). Backup envelope `App` stays `"xBrowserSyncNg"`
  so old backup files keep importing.
- v1.0.81 batch: navy top bar/drawer + white mark, item sheet without
  Show-details and uniform Cancel, verdone `#1F6F5C` accents, Theme
  System/Light/Dark setting (`xbsSetTheme` + `IStatusBarService` for OS icons),
  Logs viewer page (`/logs`, filter + share export), search re-runs after
  store rewrites (no stale counts).
- v1.0.82: explicit theme text color on native buttons (`.xbs-sort`,
  `.xbs-crumb`, `.xbs-tree-node`) — the UA button color followed the OS theme
  and went white-on-white in light mode.
  brand truncates, `padding-right: 4.5rem` reserves hamburger space
  (regression seen v1.0.63: title overlapped ☰).
- v1.0.110 redesign ("Navy ledger", canvas in the Keepmark Redesign artifact):
  tokens in `app.css` (`--km-*`, light + dark, Bootstrap vars mapped onto
  them), bundled Figtree (`wwwroot/fonts`, OFL), grouped row lists with
  `RowTile` (folder glyph / host-hashed initial), segmented sort, floating
  status pill + extended "Add" FAB that collapses on scroll (`xbs-scrolled`),
  icon action sheet, side drawer with sync card under the fixed top bar.
- Security: SQLCipher store, KeyStore creds, no telemetry, HTTPS-only,
  app lock off by default with mandatory device-PIN confirmation on enable.
- App lock (Settings → Security, `/settings/security`): relock after
  Immediately / 1 / 5 / 15 / 30 min / 1 h / 4 h / only on restart (Core
  `LockPolicy`; old free-form timeouts snap down to the stricter preset).
  Background time uses `SystemClock.ElapsedRealtime` (wall-clock changes
  can't bypass it). Locked + foreground opens the system prompt directly;
  a cancel leaves a manual Unlock button (no auto re-prompt loop).
  Enable/disable or a longer relock time needs the device PIN.
- Duplicates cleanup: **one copy of each link must always survive**
  (structural enforcement, never just a warning). Single By-link view,
  compact rows, red-✕ marking with locked 🔒 last copy, per-group + global
  delete with busy guard, swipe-right on dup rows shows details.
- Every user-facing change bumps version + is committed in `KeepmarkMobile/`
  and pushed to `origin/master`. Docs-only changes don't bump.

## Environment

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
# device offline on deploy: disconnect ghost entries, reconnect explicit
# connection refused: the wireless-debug port rotated — find the open one with
#   nmap -p 30000-49999 -T4 --open <device-ip>
# with the "Wireless debugging" screen open, mDNS advertises the device under a
# stable serial `adb-<id>._adb-tls-connect._tcp`; pass that to `-p:AdbTarget` too
# reach a local KeepmarkApi from the device (Debug builds allow loopback HTTP):
adb reverse tcp:8090 tcp:8090; curl -s localhost:8090/info   # then use http://localhost:8090 in-app
```

## Commands (from `KeepmarkMobile/`)

```sh
python3 scripts/prepare-sqlcipher.py --linux            # once: Linux x64 native prerequisites below
dotnet test                                            # Core unit + crypto/lifecycle regression tests
dotnet build src/KeepmarkMobile -f net10.0-android -c Debug
dotnet build src/KeepmarkMobile/KeepmarkMobile.csproj -t:Run -f net10.0-android -c Debug  # deploy+launch
# add "-p:AdbTarget=-s IP:PORT" when several adb entries are listed. A Debug APK
# installed with plain `adb install` aborts ("No assemblies found … Fast Deployment"):
# deploy Debug only via -t:Run. For a self-contained test APK, package Release:
dotnet build src/KeepmarkMobile -f net10.0-android -c Release -t:SignAndroidPackage -p:AndroidKeyStore=false -p:AndroidPackageFormats=apk
# AndroidKeyStore=false uses the local debug key for testing only. Check the
# APK manifest version before install; plain build may leave an older APK.
adb -s IP:PORT shell "dumpsys package com.keepmark.mobile | grep -m1 versionName"
adb -s IP:PORT exec-out screencap -p > /tmp/x.png     # screenshot
adb -s IP:PORT shell input tap 540 1200               # tap (device 1080x2412)
# workloads: MissingWorkloadPack / workloadset mismatch (SDK 10.0.x vs manifest) is fixed with
#   sudo dotnet workload repair   (fallback: update + install maui-android wasm-tools)
# verify with `dotnet workload list`; never paper over it with DOTNETSDK_WORKLOAD_* overrides.
# QA quirks: `input text` drops characters on WebView inputs — type one char at a time (~0.3s);
# `screencap` can return a corrupted frame (duplicated glyphs) right after the keyboard hides —
# just re-capture (use `dumpsys input_method | grep mInputShown` before sending BACK, which
# closes the keyboard first and only then navigates back).
```

Version bump (manual): `ApplicationDisplayVersion` 1.0.N and `ApplicationVersion`
M in `src/KeepmarkMobile/KeepmarkMobile.csproj`; CSS/JS releases also bump
`?v=` cache-buster in `wwwroot/index.html`. Commit message format:
`v1.0.N (M): short description`.

## Native SQLCipher migration (v1.0.193)

The ORM is `sqlite-net-base` 1.9.172 with `SQLitePCLRaw.provider.sqlcipher`
3.0.5. `SqlCipherRuntime` initializes the provider once; database startup fails
closed if the loaded library does not report `cipher_version`. Do not restore
`sqlite-net-pcl`, `bundle_green` or the deprecated `bundle_e_sqlcipher`.

Android uses the official SQLCipher Community Android 4.19.1 AAR runtime:
SQLCipher 4.19.0 / SQLite 3.53.4, arm64-v8a and x86_64. Binaries, SHA-256 pins,
source URLs and third-party licenses live in `native/sqlcipher/`. Since v1.0.195,
the unchanged `classes.jar` is packaged with `AndroidLibrary Bind="false"`:
`JNI_OnLoad` requires Java classes even when .NET only uses the C API.
`Xamarin.AndroidX.Sqlite` 2.6.2 supplies its Java interfaces (no SQLite engine).
SQLCipher ProGuard rules preserve these native registration roots in shrunk
builds. Omitting them aborts startup with `SQLiteCustomFunction` missing. The notices
are included as an APK asset. See [native setup](../native/sqlcipher/README.md).

Linux x64 tests and benchmarks use the same SQLCipher source release, built
with system OpenSSL. Run `python3 scripts/prepare-sqlcipher.py --linux` before
`dotnet test` on a new checkout (Python 3.12+, GCC, make, Tcl, pkg-config and
OpenSSL development files required). Generated output stays in ignored
`artifacts/native/`; Android builds use the checked-in official binaries.

No database format conversion, rekey or credential change is performed.
The synthetic original 2.1.10 fixture is opened, searched, modified and reopened
by the new engine; unkeyed reads fail. `scripts/verify-sqlcipher-compat.py --old
/path/to/old/libe_sqlcipher.so` additionally proves new-write → old-read/write
→ new-read, including FTS, in isolated processes. Rollback restores the previous
application/provider/native stack without rewriting databases.

`python3 scripts/verify-native-sqlcipher.py --apk /path/to/app.apk` verifies
the pinned Java runtime hash and all its class definitions in APK DEX, plus
both official binary hashes, ABI, 16 KiB ELF/ZIP alignment, uncompressed storage
and absence of legacy/plain SQLite libraries. Use `--abi arm64-v8a` for a
device-specific Debug APK; the default requires both supported ABIs. Android 15
arm64 startup and the existing encrypted store were verified on-device with
v1.0.195 Debug (2026-10-16); Release shrinking and battery remain unverified.
Query/code benchmark improvements in
PERFORMANCE.md are independent of this engine migration.

## Architecture map

- `src/Keepmark.Core/Api/` — XSyncApiClient, API v1.1.13, `Accept-Version` header.
- `src/Keepmark.Core/Crypto/` — PBKDF2/AES-GCM-16B-IV via BouncyCastle +
  internal LzUtf8 port of lzutf8@0.6.0. **Never change without running both:**
  Node vectors in `tests/.../TestData/vectors.json` (generated with the
  original xBrowserSync JS logic, `crypto.service.ts` + lzutf8@0.6.0) +
  the reverse check (`tools/Keepmark.Benchmarks --interop` then
  `scripts/verify-csharp-blobs.cjs`, lzutf8@0.6.0).
  Backup envelope: PBKDF2-SHA256/250k (random 16B salt stored in the file) +
  AES-128-GCM over lzutf8 payload; all import/export params validated via
  `BackupGuard` (password ≥8, path present, .json only, 50MB cap, base64
  allocation-free base64 pre-checks, tree validated ≤200k nodes, tmp cache cleaned).
  Device memory limits: encrypted envelope ≤50 MiB; decompressed UTF-8 ≤256 MiB.
  HTTP JSON streams are capped at 72 MiB (including base64 overhead); error bodies
  at 64 KiB. Limits reject extreme payloads with a localized device-limit error.
  See [performance notes](PERFORMANCE.md) for measurements and commands.
- `src/Keepmark.Core/Sync/SyncEngine.cs` — gated push/pull, background
  push, conflict logic, `DeleteBookmarkAsync`.
- `src/Keepmark.Core/Sync/BookmarkMerge.cs` — pure two-way union by URL
  (remote kept, local adopted with fresh ids, separators unioned, deletes
  never merged) + `NormalizeUrl`; same function serves preview and apply.
  `MoveResult.cs` — contextual wizard success text (mode + direction + target).
- `src/Keepmark.Core/Data/BookmarkStore.cs` — SQLCipher store, FTS5,
  duplicates query, `RestoreLocalAsync`. Pull/restore replacements rekey rows;
  ordinary pushes acknowledge blob/meta and normalize changed descriptions in
  place, preserving RowIds/FTS. UI crumbs/expansions rebase on stable `NodeId`.
  `ContentVersion` invalidates component-local derived data after mutations.
- `src/KeepmarkMobile/Services/XbsSession.cs` — session, backup export/import,
  create+upload. Connect = link/create + unlink; SwitchService = move-service
  wizard (`/switch-service`, alias `/migrate`); Backup = encrypted backup
  export/import (`/backup`).
- `src/KeepmarkMobile/Components/Pages/` — `Home` (explore, sheets, share
  consume, sort, status bar), `Duplicates`, `Connect` (link/create + unlink),
  `Backup` (encrypted export/import), `SwitchService` (move-service wizard),
  `Settings`, `Status`, `About`.
- `Components/{BookmarkSheet,BookmarkRow,FolderPicker}.razor` — edit dialog,
  row rendering, collapsible tree with Select/Cancel protocol.
- `Platforms/Android/ShareActivity.cs` — translucent, owns SEND filter,
  forwards action+type+text to MainActivity. `ShareIntake.cs` — publish/limbo/flush.
- `wwwroot/js/gestures.js` — swipe/long-press, single listener set, always
  re-points at live Home/Dup instance refs. `wwwroot/app.css` — styling + dark
  overrides (`data-bs-theme` follows system).

## Async/UI patterns (earned the hard way)

- **Optimistic delete**: remove row from local `rows`/`pinnedRows`/group lists +
  `StateHasChanged()` + `Task.Delay(1)` FIRST, then DB/sync work, `ReloadAsync()`
  after. List must react instantly, never wait for popup/toast.
- **Busy before toast**: set `busy=false` + `StateHasChanged()` BEFORE
  `Toast.ShowAsync` (toast waits ~3.5s; buttons must re-enable immediately).
- Fire-and-forget no-op JS: `_ = JS.InvokeVoidAsync("xbsGestures.closeAll")`,
  never `await` it before UI work.
- Modal sheets lock page scroll: `xbsGestures.lockScroll(bool)` toggles
  `xbs-locked` on html/body; backdrop has `touch-action: none`, sheet
  `touch-action: pan-y`, both `overscroll-behavior: contain`. Home syncs the
  lock in `OnAfterRenderAsync` and releases it in `Dispose`.
  The nav drawer (`NavMenu` checkbox `@onchange`) uses the same lock,
  owner-keyed (`"sheet"` / `"nav"`): the page stays locked while ANY holder
  asks for it.
- Toast is fire-and-forget on the UI path: every action renders its list
  update (`StateHasChanged`) BEFORE `_ = Toast.ShowAsync(...)`. Never `await`
  a toast while list state is still unpainted (Blazor paints the handler's
  renders only around explicit `StateHasChanged`, so awaiting first delays
  the visible update by the toast duration).
- Share consume: peek-before-consume in `Home.OpenSharedIfPending` via
  `ShareIntake` choke point (limbo for cold start).
- Auto-mode push uses one worker with 250 ms coalescing and dirty/conflict/mode
  checks under the engine gate; UI closes sheets right after the durable local write.
- `XbsSession.InitAsync` shares one initialization task. Components await it with
  their lifetime token; never register singleton events or create timers after disposal.
- `Window.Activated/Resumed/Stopped/Destroying` control visibility. Auto polling
  runs only while foreground + Auto, first after 500 ms, then 15 minutes after the
  previous tick completes. Stop cancels reads/retries and drains the worker.
  Already-started edit uploads may finish and acknowledge their outcome.
- Home's relative-time clock stops in background. Global folder/count/pin data is
  refreshed only after `ContentVersion`, language, pins or relevant settings change.
  Uniform collapsed folder lists over 100 rows virtualize; duplicate groups page in batches of 100; explicit location jumps
  temporarily reveal all rows until the next navigation to preserve DOM anchors.
- Gesture registrations are owner-keyed and released with their component;
  layout measurements are coalesced into one animation frame.

## Engineering rules (non-negotiable)

- **Efficiency**: batch work into the fewest transactions and network calls.
  Never loop single-item operations when a bulk path exists — one sync storm
  can get the service to throttle or blacklist us (it has anti-abuse limits).
  Example: duplicates cleanup deletes everything in ONE store transaction +
  ONE push, never one push per row.
- **Safety**: destructive work resolves by stable identity (NodeId, never a
  possibly-rekeyed RowId), validates before acting, keeps an undo or
  safety-backup path, and never swallows errors silently — log them with context.
- **Atomicity**: multi-step mutations go in a single transaction where SQLite
  allows it (resolve + delete inside one `tran`), so a concurrent push/pull
  rekey cannot strand half a batch.

## Razor/blazor gotchas

- String component params without `@` pass literals.
- Ternary with `: null` or nested ternaries inside attributes fails to parse —
  use `@code` helpers.
- Blazor renders bool ARIA as `True/False` — must lowercase.
- Inside `@if { }` blocks Blazor drops the whitespace between and before
  inline expressions and `<text> …</text>` (seen live as `2 KBof 1 MB`,
  `3Resolve`): build the run as one string, `@($" {a} {b}")`, or emit `@(" ")`.
- URL inputs need `autocapitalize="off" autocorrect="off" spellcheck="false"`
  (Android capitalized `Https://`); store via `WorkflowRules.NormalizeServiceUrl`.
- sqlite-net enables shared-cache; SQLCipher `PRAGMA key` needs ExecuteScalar.
- AndroidX Biometric forbids custom negative button with DEVICE_CREDENTIAL.

## Restart checklist

1. `cd $HOME/Projects/xBrowserSync/KeepmarkMobile && git log --oneline | head -3`
2. Set env (above), `adb connect`, `dotnet test`.
3. Deploy `-t:Run`, verify `versionName`, screenshot.
4. Deploy restarts the app → device shows lock screen if auto-lock on; unlock manually.

## Development requirements (prevent regression)

### Concurrency & gate discipline

- **Every `await _gate.WaitAsync()` MUST have a matching `_gate.Release()` in a `finally` block.** No exceptions.
- **Conflict resolution methods** (`ResolveConflictPullAsync`, `ResolveConflictPushAsync`, `ResolveConflictMergeAsync`) MUST:
  1. Set `_resolvingConflict = true` at start of `try`
  2. Reset `_resolvingConflict = false` in `finally` (after `_gate.Release()`)
- **`ForceSyncAsync` and `PushInBackgroundAsync`** MUST check `_resolvingConflict` after acquiring `_gate` and return early if true.
- **Background timers** MUST NOT fire sync logic while `_resolvingConflict` is true.

### Merge retry discipline

- **Merge push MUST retry on 409** with re-fetch + re-merge (max 3 attempts).

### State capture completeness

- **All conflict entry points** must capture conflict state for UI resolution.
- **Preview API** MUST re-fetch remote at call time.

### Test coverage requirements

- **Concurrency tests**: race between auto-sync and conflict resolution
- **Merge retry test**: simulate 409 on push, verify re-merge + retry
- **Gate release test**: every method acquiring `_gate` releases in `finally`
- **Flag test**: `_resolvingConflict` prevents auto-sync during resolution

## Safety backups (behavioral spec — verifiable by a third party)

Every destructive operation snapshots the side it is about to destroy
("backup what you destroy") BEFORE mutating anything, and tells the user
where the snapshot went. Snapshots live in `safety-backups/` (app-private),
are listed/restored/deleted from Backup → Safety backups, and are
encrypted except legacy/pre-link files (flagged "not encrypted" in the UI).

### 1. What must be backed up (per flow)

| Flow | Destroyed side | Snapshot content | Operation tag |
|---|---|---|---|
| Connect → take remote | local tree | current local JSON | `take-remote` |
| Connect → upload local | server copy | current remote JSON | `upload-local` |
| Connect → merge both sides | both sides | current local JSON + current remote JSON | `connect-merge` |
| Connect → create empty | local tree | current local JSON | `create-empty` |
| Import encrypted backup | local tree | current local JSON | `import-backup` |
| Change password | server copy | current remote JSON | `change-password` |
| Switch → take remote | local tree | current local JSON | `switch-take-remote` |
| Switch → upload local | server copy | current remote JSON | `switch-upload` |
| Switch → merge both sides | both sides | current local JSON + current remote JSON | `switch-merge` |
| Duplicates bulk delete | local tree | pre-delete local JSON | `duplicates-cleanup` |
| Safety restore (any) | current state | state being replaced | `pre-restore` |
| Conflict → take remote | local tree | current local JSON | `conflict-pull` |
| Conflict → upload local | server copy | current remote JSON | `conflict-push` |
| Conflict → merge both sides | both sides | current local JSON + current remote JSON | `conflict-merge` |

Rule: no snapshot → the operation must not run (fail closed, error surfaced).
Exception: single-bookmark delete on Home keeps its 6-second in-memory undo
instead of a file (documented, no snapshot required).

### 2. File format, naming, manifest

- Payload: bookmark-tree JSON (same shape as sync blobs, plaintext) or, when
  an aligned link key exists, AES-GCM envelope via `XCryptoService`
  (see §3). Extension always `.json`.
- Filename: `safety-{yyyyMMdd-HHmmss}-{operation}[-{n}].json`
  (human-readable in file managers; `-{n}` disambiguates same-second writes).
- Manifest `safety-index.json`: array of
  `{FileName, CreatedAtUtc, Operation, Side, Items, SizeBytes, SyncId, Enc}`.
  `SyncId` is truncated (`ab12…ef`); `Enc` is `"master"` or absent (plaintext).
- Legacy files (`safety-{local|remote}-{ts}.json`, no manifest entry) MUST
  still be listed, restorable and deletable, shown as "Older backup".
- Retention: newest 5 kept, older pruned automatically (file + index row
  removed together). Manual delete allowed anytime with two-tap confirm.
- Corrupt index → treated as empty (legacy scan still lists files).
  Damaged payload → open refuses with "damaged", never half-restores.

### 3. Encryption (envelope, Master Key)

- Files are encrypted ONCE under a random 256-bit Master Key
  (`XCryptoService.Encrypt`, same envelope as sync crypto).
- The Master Key record (`safety-master.json`) holds ONLY wrapped copies:
  `[{KekId, WrappedKey}]`, newest first, max 3. KEK = current sync-derived
  key; `KekId` = first 8 hex of SHA-256(KEK). The plaintext master never
  touches disk.
- Pre-link / misaligned writes fall back to plaintext flagged `Enc: absent`
  ("not encrypted" in UI) — never silently, never assumed encrypted.
- Rotation (switch success, password-change success): unwrap with old KEK,
  wrap with new KEK, write temp + fsync + atomic rename, then test-unwrap
  before trusting; previous file kept as `.prev` until verify passes.
  Rotation failure must NEVER fail the user flow (warn + continue).
- Atomicity contract: rotation is ONE small atomic write; backup blobs are
  never re-encrypted. A crash can only leave (a) old master intact, or
  (b) new master verified — never a torn master. Concurrent access is
  serialized by a session gate; misaligned states refuse honestly
  ("locked to another sync (…)" naming the origin sync) instead of guessing.
- Unlink drops the key from memory; deterministic PBKDF2 means re-linking
  the same sync+password re-opens everything with no recovery flow.

### 4. Restore semantics

- Local-side entry: pre-restore snapshot of current local first, then
  replace local tree (dirty → auto-push, or honest 409 later). Works unlinked.
- Remote-side entry: requires an active link (refuse otherwise: "Link the
  sync this backup belongs to first"); pre-restore snapshot of current
  remote; re-encrypt with the CURRENT link key; push (revision CAS or
  timestamp; 409 surfaces, nothing is clobbered). The entry's origin sync
  is shown so the user never uploads old data onto the wrong sync silently.
- Restore never deletes the used entry. Counts shown before confirm must
  match `ValidateTree` (bounded tree, no nulls).

### 5. UI obligations

- Every destructive success message names the safety net and where to find
  it ("… — see Backup → Safety backups").
- Safety list rows show: local time, operation description, side
  (local tree / server copy), item count, size, origin sync, encrypted flag.
- Restore and Delete both require explicit confirm; destructive errors show
  the reason verbatim, secrets are cleared from inputs on failure.
- Change-password and switch-existing keep their current-password /
  verify-first gates; nothing in this spec weakens them.
- Wizard error evidence belongs to the step that produced it: Connect and
  Switch service render one error box per action cluster
  (check / verify-link / apply-review) adjacent to its button
  (`role="alert"`), never a single shared box.
- Multi-outcome successes restate WHAT ran (mode + direction + target) via
  the pure `MoveResult.Describe` helper (unit-pinned in
  `BookmarkMergeTests`), never a bare "Done".
- Merge is always preview-locked: Preview shows the union counts, changing
  direction or re-verifying invalidates the preview, and apply refuses
  without a fresh preview for the current probe.

### 6. How to verify (third-party checklist)

1. `dotnet test` green (incl. `SafetyBackupTests`: naming, describe,
   prune-5, legacy parse, index round-trip/damage, fingerprint, wrap trim).
2. Trigger each flow in §1 → Backup → Safety backups shows a new row with
   the right operation/side/count; filename matches on disk.
3. Restore a local entry → tree replaced, pre-restore row appears, count matches.
4. Restore a remote entry unlinked → refused with the link message.
5. Delete a row → file + index row gone; restore-then-delete of the same
   name → "not found".
6. Switch service / change password → previous rows still open
   (re-wrapped, same origin sync shown).
7. Unlink → encrypted rows refuse with the origin sync named; re-link same
   credentials → they open again.
8. Kill the app mid-rotation (or restore `.prev` over master) → master
   intact or verifiable, no silent lockout; index damage → list falls back
   to directory scan.
9. Merge (Connect and Switch service): Preview counts match the applied
   result (same pure `BookmarkMerge` function serves both); after apply,
   TWO new rows appear (`connect-merge` / `switch-merge`, sides local +
   remote) with the right counts; tree holds the union, nothing deleted.
10. Merge preview-lock: preview, then change direction and back (or
    re-verify) → apply refuses with "Preview the merge first — …".
11. Merge wrong-password: verify with a wrong password → `InvalidCredentials`,
    zero mutations, zero safety rows.

## Compatibility

- Server: any xBrowserSync API v1.1.13 service (official
  `https://api.xbrowsersync.org`, self-hosted or community), min API v1.1.9.
  `Accept-Version` header pinned to v1.1.13 (satisfies the server `^1.1.3` route).
- Sync format v1.8.0 (≥ latest known official client): syncs open both ways.
- Crypto (PBKDF2-SHA256/250k + AES-128-GCM-16B-IV + lzutf8) verified
  byte-for-byte against Node in both directions — blobs are interchangeable
  with the official clients.
- Same guarantees: end-to-end encrypted, operators can't read data.

## License and notices

License, third-party notices and disclaimer: see the [README](../README.md#license).

## Browser QR connection (v1.0.200)

Connect → Link existing and Move service → target service / existing target
place a QR icon and Scan QR from browser before the manual credential fields.
Both flows use the native
`IQrScanner` / `QrScanner` modal, pinned `ZXing.Net.Maui.Controls` **0.10.4**
(net10.0-android). CAMERA is requested only on the scan action; camera hardware
is optional. No photographs, gallery, clipboard or image import are used.
The scanner only decodes QR, with UTF-8; DisconnectHandler stops detection,
unsubscribes events, disconnects/disposes CameraManager and unbinds CameraX.
Cancellation, Android Back, modal disappearance, app lock/background and page
disposal release the session. `AppLockState.Changed` now includes foreground
transitions, so native content cannot remain over the lock overlay.

`Workflow/ConnectQrPayload` validates the Keepmark JSON v1 allowlist before
network access: 1,800 UTF-8 bytes, strict markers/types/duplicate names/depth,
safe ID, supported numeric sync version, HTTPS URL without credentials,
query/fragment/backslash/controls. Debug alone accepts HTTP localhost/127.0.0.1.
Its representation is redacted; passwords remain literal and are never stored.
A valid scan only produces an in-memory candidate. Both pages render the
shared `QrCredentialReview` exclusively while `ConnectQrCandidateState.ShowReview`
is pending or verifying: service URL and abbreviated sync ID, acquired-credentials
message, and separate, spaced primary/cancel actions. Manual credentials,
service controls and subsequent wizard headings (including “Where?”) stay
hidden. Explicit verification switches the same review to the localized
“Verifying sync…” status; `BeginVerification` consumes the raw secret without
removing the review, so no manual form or next-stage heading flashes during
the probe. Reconciliation/direction controls appear only after verification
succeeds; failures return to a cleared candidate and adjacent safe error. Connect explicitly calls the same ProbeLinkAsync/reconcile/apply path as
manual entry, including backups and the final informed action. SwitchService similarly uses
its existing VerifyExistingCredentials/probe and service-check path, then its
normal direction/review/confirmed move. A scan never bypasses the linked-user
gate or commits a move. The pending secret clears
on consumption, failure, edits, cancellation, background, lock or 60-second
local timeout. This timeout does not expire credentials or invalidate copies.

Core parser and `ConnectQrCandidateState` tests run without Android. The
shared candidate state rejects stale/duplicate scans, expires against monotonic
time at consumption and removes its secret before probing. Debug and Release builds cannot verify
camera permission, real UTF-8 decoding, rotation, camera-indicator shutdown,
biometrics or device shrinking behavior: those require a real device.


## Multiple selection (v1.0.201)

Long press (500 ms) or ⋮ → Select captures the current list and content version.
Checkboxes replace icons; tap/Enter/Space toggles selection. All/None applies to
loaded logical candidates, including virtualized rows; search is limited to the
50 returned results. Pinned/recent aliases deduplicate by NodeId. Folder selection
includes its subtree; overlapping search descendants are covered, not counted twice.
Top-level system containers and separators cannot be selected directly.
Attempting to start selection on them shows an immediate informational toast,
without leaving a persistent error below the list.

The contextual pagebar and bottom Move/Delete actions freeze folder/search navigation.
Back exits without mutation; Back in a destination picker/confirmation closes only
that layer. Background/lock cancels pending interaction. Shared intake remains pending.
A content change invalidates selection before it can act on stale row identities.
The gesture captures NodeId and version at pointerdown; accepted long press triggers
Android native LongPress haptics respecting system settings plus a visual checkbox
transition. Reduced motion disables the transition; vertical scrolling stays available.

`Home.Selection.cs` coordinates `SelectionController`, store `SelectionPlan`, and
gated engine batch APIs. IDs are resolved and validated inside the same SQLite
transaction as the mutation; one engine notification follows the batch. Move appends
roots in captured display order and skips roots already in the destination. No-op
batches create no backup and do not notify sync. Undo lasts six seconds and restores
only the batch positions; any intervening content change refuses the whole undo.
Delete has one confirmation with separate selected-root and contained-item counts;
it offers persistent safety backup recovery rather than a global-tree undo.

MovePicker is shared by single and batch flows. Single and batch moves save successful folder destinations in the same persistent
six-entry history (stable NodeIds, most recent first). Both pickers show up to
four valid recent destinations, excluding no-op and forbidden destinations;
tapping a recent destination moves directly, while the tree requires confirmation.
The batch summary appears only when some or all items are already at the target.
One source folder is shown by path. Move results omit zero counts and show the
destination, with Undo instead of a redundant backup reminder. Select a destination, then New folder
in that path; enter name and Create. The folder is saved immediately and selected.
Canceling the picker leaves a created folder intact. Move remains disabled while the
name is a draft. Batch folder creation is gated against the expected version and
returns the new version explicitly, preserving selection without ignoring external
sync changes. Selected folder subtrees are excluded from destinations.

Move/delete snapshot the local tree under the engine gate, write and read-verify the
safety backup before mutation (`selection-move`, `selection-delete`; undo uses
`selection-move-undo`). Backup failure leaves the batch untouched. A successful local
write does not claim the network is synchronized. Test coverage: SelectionBatchTests,
SelectionControllerTests and `node tests/js/gestures.test.cjs`. TalkBack, physical
haptic perception, font scaling and rotation require device QA.

Move-picker layout: original tree layout retained, with the approved “Cartella incisa” icon
(closed folder pocket and fine outline on the existing tinted tile).
Single and batch flows share wrapping names, capped visual nesting, 44px expand
targets, explicit Cancel/Move actions and a separate wrapping destination path.
New-folder entry uses a full-width field above Create/Cancel; destination/search
controls pause while a folder-name draft is open, preventing accidental draft loss.

The incised folder icon is shared visually by FolderPicker (including search and
drafts), MovePicker, recent move shortcuts and the bookmark form folder button.

## Service selection (v1.0.208)

Connect (existing/new) and Switch service share `ServiceSelector`. The bundled
eight-service catalog keeps api.xbrowsersync.org first and default; country codes,
sync limits and API versions are advertised reference data. Selection probes only
the chosen endpoint; automatic checks gate continuation and unavailable or
incompatible services cannot advance. NoNewSyncs permits existing links only.
Custom URLs expose manual verification, with inline live server metadata. Input
changes invalidate results; `Workflow/ServiceCheckState.cs` (generation,
readiness, create permission) rejects stale responses, and the scoped
`ServiceSelector.razor.css` styles the collapsed button and the expanded choice
list. `XbsSettings.SavedServices` remembers custom HTTPS endpoints only from successful
session apply/create methods, never probes or reconciliation previews. Saved
entries remain selectable across flows even after temporary outages.

Saved service rows retain verified location, limit and API version after a
successful pairing. Subsequent checks may refresh this endpoint metadata only
for already-saved URLs. Legacy saved entries show unknown metadata until their
next check. URL identities canonicalize host case/default ports while retaining
path case, so equivalent addresses cannot create duplicate entries.
