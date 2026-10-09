# Keepmark for Android (KeepmarkMobile)

Keepmark is private, end-to-end encrypted bookmark sync: an **independent
alternative compatible with xBrowserSync**. It works with any xBrowserSync
service (official, self-hosted or community) and with the official
xBrowserSync apps: syncs created with Keepmark open there, and vice versa.
Keepmark is not affiliated with or endorsed by xBrowserSync.

The Keepmark family:

- **Keepmark for browsers** — Firefox and Chromium-based browsers (`KeepmarkApp`).
- **Keepmark for Android** — Android 10+ app (`KeepmarkMobile`).
- **Keepmark Server** — drop-in xBrowserSync-compatible sync service with
  opt-in extended features (`KeepmarkApi`).

This repo is the Android app (MAUI Blazor Hybrid, Android 10+ / API 29).

## Features

- **End-to-end encryption**: bookmarks are encrypted on the device
  (PBKDF2-SHA256 + AES-GCM); the password never leaves the phone and the
  service operator cannot read the data.
- **Browse and search** (full-text), add, edit, delete; pinned section,
  clickable breadcrumb, folder-first sorting, swipe for details.
- **Share to Keepmark** from any app: the add form opens prefilled.
- **Automatic or manual sync**; conflicts are never resolved silently —
  download / upload / merge, merge with preview.
- **Safety backups**: automatic encrypted snapshot before every destructive
  step (newest 5 kept), restorable from Backup → Safety backups.
- **Encrypted backup** export/import, file compatible with Keepmark for browsers.
- **Duplicates cleanup**: one copy of each link always survives.
- **Move service**: move your bookmarks to another service, new or existing sync.
- **App lock** with fingerprint or device PIN, configurable relock time.
- **Local data encrypted at rest** (SQLCipher); credentials in the Android KeyStore.
- Light / dark / system theme, logs viewer with share export.
- **Extended features with Keepmark Server (v1.2)** with automatic fallback
  on legacy services.
- **Languages**: English, Italiano, Français, Español, Deutsch — follows the Android language, override in
  Settings → Language.
- **Privacy**: no telemetry, HTTPS-only services.

## Developer guide

> Behavioral spec for outsourced code verification:
> [`docs/verification-spec.md`](docs/verification-spec.md) (requirements Rxx —
> read it before reviewing code).

Talks to any xBrowserSync API **v1.1.13** service (min v1.1.9), plus the
v1.2 extended contract when advertised. Current version: **v1.0.147 (148)**,
app id `com.keepmark.mobile`, display name `Keepmark`.

`KeepmarkMobile/` is its own git repo (root `$HOME/Projects/xBrowserSync`
is NOT a git repo). Solution `KeepmarkMobile.slnx`:
`src/KeepmarkMobile` (MAUI Blazor, `net10.0-android` only, min API 29),
`src/Keepmark.Core` (net10.0, UI-free logic),
`tests/Keepmark.Core.Tests` (xUnit).

## Requirements (load-bearing)

- Android only, API 29+. UI in en/it/fr/es/de: default follows the Android language, override in Settings → Language (reloads the UI). Logs stay English. Every UI text is a key in `Keepmark.Core/I18n` (`Str` + `Catalog.*.cs`, parity checked by `I18nTests`); wording follows `docs/UI-TERMS.md`.
- Sync: auto default + manual mode; **409 conflict never silently merged**;
  safety backup before overwrites; display sort never affects sync order;
  background push serializes with pull on the engine gate.
- UX: tap opens, ⋮ menu holds all actions, swipe right expands details /
  left collapses, pinned section, clickable breadcrumb, folder-first sorting,
  share-receiving with prefilled form, full-screen folder tree picker with
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

Device is WiFi-debug, IP:port rotates (last `<device-ip>:<port>`). Reconnect:

```sh
adb disconnect; adb connect <device-ip>:<port>; adb devices
# pairing port ≠ service port: `adb pair IP:PAIRPORT` (code), then `adb connect IP:SVCPORT`
# mDNS ghosts cause XA0010: `adb disconnect <ghost>` or `adb disconnect` (all), use explicit `-s IP:port`
# device offline on deploy: disconnect ghost entries, reconnect explicit
```

## Commands (from `KeepmarkMobile/`)

```sh
dotnet test                                            # Core unit + interop (182 tests)
dotnet build src/KeepmarkMobile -f net10.0-android -c Debug
dotnet build src/KeepmarkMobile/KeepmarkMobile.csproj -t:Run -f net10.0-android -c Debug  # deploy+launch
# add "-p:AdbTarget=-s IP:PORT" when several adb entries are listed. A Debug APK
# installed with plain `adb install` aborts ("No assemblies found … Fast Deployment"):
# deploy Debug only via -t:Run, or build Release for a self-contained APK.
adb -s IP:PORT shell "dumpsys package com.keepmark.mobile | grep -m1 versionName"
adb -s IP:PORT exec-out screencap -p > /tmp/x.png     # screenshot
adb -s IP:PORT shell input tap 540 1200               # tap (device 1080x2412)
```

Version bump (manual): `ApplicationDisplayVersion` 1.0.N and `ApplicationVersion`
M in `src/KeepmarkMobile/KeepmarkMobile.csproj`; CSS/JS releases also bump
`?v=` cache-buster in `wwwroot/index.html`. Commit message format:
`v1.0.N (M): short description`.

## Architecture map

- `src/Keepmark.Core/Api/` — XSyncApiClient, API v1.1.13, `Accept-Version` header.
- `src/Keepmark.Core/Crypto/` — PBKDF2/AES-GCM-16B-IV via BouncyCastle +
  internal LzUtf8 port of lzutf8@0.6.0. **Never change without running both:**
  Node vectors in `tests/.../TestData/vectors.json`
  (generated by `/tmp/opencode/xbs-crypto/gen-vectors.cjs`) +
  reverse check `/tmp/opencode/xbs-crypto/verify-csharp-blobs.cjs`.
  Backup envelope: PBKDF2-SHA256/250k (random 16B salt stored in the file) +
  AES-128-GCM over lzutf8 payload; all import/export params validated via
  `BackupGuard` (password ≥8, path present, .json only, 50MB cap, base64
  pre-checks, tree validated ≤200k nodes, tmp cache cleaned).
- `src/Keepmark.Core/Sync/SyncEngine.cs` — gated push/pull, background
  push, conflict logic, `DeleteBookmarkAsync`.
- `src/Keepmark.Core/Sync/BookmarkMerge.cs` — pure two-way union by URL
  (remote kept, local adopted with fresh ids, separators unioned, deletes
  never merged) + `NormalizeUrl`; same function serves preview and apply.
  `MoveResult.cs` — contextual wizard success text (mode + direction + target).
- `src/Keepmark.Core/Data/BookmarkStore.cs` — SQLCipher store, FTS5,
  duplicates query, `RestoreLocalAsync`. Rows rekeyed by every push/pull
  `ReplaceAll`: UI crumbs/expansions rebase on stable `NodeId`.
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
- Auto-mode push runs fire-and-forget; UI closes sheets right after local write.

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

## License

Proprietary — © 2026 Yari Melzani, all rights reserved. See `LICENSE.md`.
Independent implementation, written from scratch; compatible with the
xBrowserSync protocol. Not affiliated with or endorsed by xBrowserSync. Third-party notices: `Crypto/LzUtf8.cs` is a C# port of
`lzutf8` (MIT, © Rotem Dan, notice kept in the file); BouncyCastle (MIT).

Project home (releases, issues, documentation): <https://github.com/Keepmark>.

## Disclaimer

Keepmark is provided "as is", without warranty of any kind, express or
implied, including the implied warranties of merchantability and fitness for
a particular purpose. Use it at your own risk: the authors are not liable for
any loss of data (including bookmarks), damage or other consequences arising
from its use. See `LICENSE.md`. Keep your own backups.

## Compatibility

- Server: any xBrowserSync API v1.1.13 service (official
  `https://api.xbrowsersync.org`, self-hosted or community), min API v1.1.9.
  `Accept-Version` header pinned to v1.1.13 (satisfies the server `^1.1.3` route).
- Sync format v1.8.0 (≥ latest known official client): syncs open both ways.
- Crypto (PBKDF2-SHA256/250k + AES-128-GCM-16B-IV + lzutf8) verified
  byte-for-byte against Node in both directions — blobs are interchangeable
  with the official clients.
- Same guarantees: end-to-end encrypted, operators can't read data.
