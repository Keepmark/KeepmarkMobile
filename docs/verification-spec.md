# Keepmark — Verification Specification (for outsourced code review)

This document states the REQUIRED behavior of the Keepmark Android app in
verifiable form. A third-party reviewer checks the code (and the running app)
against each numbered requirement. Where the code is known to violate a
requirement, it is marked **[KNOWN GAP]** instead of being silently adjusted.

Conventions: `Rxx` = requirement IDs. "Must" = fail the review if violated.
File paths are relative to `KeepmarkMobile/`. Commands run from `KeepmarkMobile/` with
`ANDROID_HOME=$HOME/android-sdk`, `JAVA_HOME=$HOME/jdk-21`
(JDK 21; system JDK is rejected): `dotnet test` (xUnit, no device),
Android build `dotnet build src/KeepmarkMobile -f net10.0-android -c Debug`.

Global rules (verify everywhere): UI strings in en/it/fr/es/de via `Loc`/`IXbsLocalizer` (log strings stay English); no telemetry;
HTTPS-only service URLs (reject `http://` except `localhost` for dev);
no `any`-style dynamic typing leaks into persisted state; raw passwords exist
only as method arguments, never stored, never logged.

## 1. Interop constants (`src/Keepmark.Core/XbsConstants.cs`)

| R | Constant | Required value / behavior |
|---|---|---|
| R1.1 | `DefaultServiceUrl` | `https://api.xbrowsersync.org` |
| R1.2 | `MinApiVersion` | `1.1.9` — services below are refused (R4.5) |
| R1.3 | `ApiVersion` | `1.1.13` — default `Accept-Version`, satisfies server `^1.1.3` route |
| R1.4 | `ApiVersion12` | `1.2.0` — extended contract, sent ONLY after capability probe (R4.2) |
| R1.5 | `SyncClientVersion` | `1.8.0` — advertised on create/upgrade; remote `version` greater than this MUST be refused (R4.6) |
| R1.6 | `KeyDerivationIterations` | `250_000` (PBKDF2-SHA256) |
| R1.7 | `GcmNonceLength` | `16` bytes, prepended to ciphertext (NOT the 12-byte .NET default) |
| R1.8 | `DescriptionMaxLength` | `300` chars, enforced on push (truncate) |
| R1.9 | `SeparatorUrl` | `xbs:separator` |
| R1.10 | `SyncPollInterval` / `ServiceProbeTimeout` / `DefaultHttpTimeout` | 15 min / 3 s / 12 s |
| R1.11 | `CompareVersions` | numeric dotted compare, tolerates leading `v` (`1.1.13` > `1.1.9`); unit-pinned in `VersionCompareTests.cs` |

## 2. Crypto (`src/Keepmark.Core/Crypto/`)

| R | Requirement | How to verify |
|---|---|---|
| R2.1 | Envelope = `base64(IV16B \|\| AES-GCM-256(lzutf8(JSON)))`; key = PBKDF2-SHA256/250k over UTF-8(password), salt = UTF-8(syncId) | `XCryptoService.cs:DeriveKey/Encrypt/Decrypt`; BouncyCastle required (16B IV — .NET `AesGcm` only does 12B) |
| R2.2 | Byte-interop BOTH directions with `lzutf8@0.6.0` + original JS client | `CryptoInteropTests.cs` + `TestData/vectors.json` green; never change `Crypto/` without running both Node directions |
| R2.3 | Legacy sync without version rejected ("open once in the official xBrowserSync client") | `DeriveKey` throws `UnsupportedSyncVersionException` on empty version |
| R2.4 | Decrypt failure (wrong password / corrupt) surfaces as `InvalidCredentialsException`, never as generic error | Code + wrong-password tests |
| R2.5 | Encrypted backup envelope `{App:"xBrowserSyncNg", Format:1, Salt:Base64(16B random), Blob}`; password ≥ 8, file ≤ 50 MB, tree ≤ 200k nodes (`BackupGuard`) | `BackupGuardTests.cs`; damaged-file vs wrong-password distinguished (base64 pre-check) |

## 3. API client (`src/Keepmark.Core/Api/XSyncApiClient.cs`)

| R | Requirement |
|---|---|
| R3.1 | Endpoints used: `GET info`, `POST bookmarks {version}`, `GET bookmarks/{id}`, `GET …/lastUpdated`, `GET …/version`, `PUT bookmarks/{id}` (timestamp or `baseRevision` CAS), conditional `GET` with `If-None-Match: "<hash>"` → `304` = null. NO server-side sync DELETE exists in this client. |
| R3.2 | `Accept-Version` per request: `1.1.13` default; `1.2.0` only for `GetInfoV12Async` (capability probe), `GetBookmarksV12Async` (full GET with `meta`, used only after a positive probe), `UpdateBookmarksV12Async` and conditional GET. |
| R3.3 | Timeouts: 12 s default, 3 s probes; timeout/`HttpRequestException` → `ServiceOfflineException`. Retries with backoff ONLY for idempotent reads (never on create/PUT — see `WithRetryAsync(idempotent:)`); `Retry-After` honored. |
| R3.4 | HTTP→error mapping: 401 SyncNotFound, 404 InvalidService, 405 NotAcceptingNewSyncs, 406 DailyNewSyncLimit, 409 DataOutOfSync, 413 SyncTooLarge, 429 Throttled (+Retry-After secs), 5xx/other ServiceOffline. `code`/`message` shape preserved for display. |
| R3.5 | Bodies never logged; sync IDs truncated via `LogSanitizer` (`abcd…xy`); timings + byte counts at Info ok. |

## 4. Sync engine (`src/Keepmark.Core/Sync/SyncEngine.cs`)

| R | Requirement |
|---|---|
| R4.1 | Whole-blob only (API has no deltas). `Mode` Auto (poll + push-on-edit) / Manual (explicit only). Statuses: Idle/Syncing/Offline/Conflict/Error/NoCredentials + `DataChanged`/`StatusChanged` events; UI preserves scroll/query across events. |
| R4.2 | v1.2 capability probed via `/info` capabilities per service URL, cached; legacy → timestamp path. Conditional GET (304) skips download when hash known. Revision CAS on push when revision stored; 409 on races, never overwrite. |
| R4.3 | v1.2 capability probed on the 1.2.0 contract (`GetInfoV12Async` — the legacy contract strips capabilities, so probing there could never succeed), cached per service URL; 412 / missing capabilities = legacy. A service older than MinApiVersion throws instead of being silently used. Verifier: against a v1.2 server, conditional GET and revision CAS must be observed in logs. |
| R4.4 | Pull MUST refuse when the mirror is dirty (unpushed edits): status Conflict + `DataOutOfSyncException`, never silent discard. |
| R4.5 | `ForceSyncAsync` matrix: remote≠local + dirty → Conflict (no auto-merge); remote≠local + clean → pull; equal + dirty → push; equal + clean → stamp `LastSyncAt`. Offline → Offline status, no throw to UI. |
| R4.6 | **Conflict resolution**: when status is Conflict, engine exposes `ResolveConflictPullAsync`, `ResolveConflictPushAsync`, `ResolveConflictMergeAsync`, and `GetConflictMergePreviewAsync`. UI MUST present 3 choices (Pull/Push/Merge) with preview counts from `GetConflictMergePreviewAsync`. Safety backup of destroyed side(s) taken BEFORE resolution (R7.1). Pull replaces local with remote; Push overwrites remote with local; Merge unions both sides (never deletes). |
| R4.7 | Version gates: service < MinApiVersion refused at check; remote sync format > SyncClientVersion refused on pull/compare. |
| R4.8 | `Normalize` on push: empty description/tags → null, descriptions truncated to 300 chars (byte-compatible with the official xBrowserSync clients). |
| R4.9 | Bulk delete by stable NodeId = ONE transaction + ONE push (no sync storms). Single delete returns the subtree for 6 s undo.

## 5. Credentials & local store

| R | Requirement |
|---|---|
| R5.1 | Android KeyStore-backed `SecureStorage` keys ONLY: `xbs_sync_id`, `xbs_service_url`, `xbs_derived_key` (base64, NEVER raw password), `xbs_sync_version`, `xbs_db_key` (64 hex, created once). `ClearAsync` removes the 4 sync keys, keeps DB key. |
| R5.2 | Non-secrets in `Preferences` (`XbsSettings.cs`): service URL, sync mode, verbosity, app lock + timeout, theme, open mode, list sort. |
| R5.3 | SQLCipher mirror `Meta`/`Blob`/`Items` (+FTS5): `lastUpdated`, `syncVersion`, `syncRevision`, `syncHash`, `dirty` ("1"/"0"); `Blob.bookmarks` = last encrypted blob. `ReplaceAll` clears revision/hash on legacy pulls. |

## 6. Session flows (`src/KeepmarkMobile/Services/XbsSession.cs`)

Each flow states preconditions, mutations in order, and the REQUIRED user-visible outcome. Raw passwords are method args only, cleared in UI `finally`.

| R | Flow | Required behavior |
|---|---|---|
| R6.1 | Create empty (`CreateNewSyncAsync`) | Safety-backup local → POST → derive → store creds → replace mirror EMPTY. UI arms with explicit confirm. Message states the backup + where to find it (R7). |
| R6.2 | Create with upload (`CreateNewSyncWithUploadAsync`) | POST → derive → store creds → upload mirror. Nothing local deleted. Returns new ID for display/copy. |
| R6.3 | Link existing, pull (`ProbeLinkAsync` + `ApplyRemoteAsync`) | Password verified by decrypting remote BEFORE any overwrite (wrong → `InvalidCredentials`, zero mutations). Safety-backup local side. `0/0` items → auto-apply; else explicit Download/Upload/Merge choice with counts. |
| R6.4 | Link existing, push (`ApplyLocalAsync`) | Same verify-first; safety-backup remote side; PUT on remote base (409 honest). |
| R6.4b | Link existing, merge (`GetMergePreviewAsync` + `ApplyMergeAsync`, `connect-merge`) | Same verify-first. Preview is a dry-run of the same pure union (`BookmarkMerge`): local-only upload / remote-only download / title-conflict (server wins) / folders-created / separators counts. Preview is locked: changing direction or re-verifying invalidates it. Apply snapshots BOTH sides first (fail closed), then writes the union locally and pushes (409 honest, both snapshots intact). Merge never deletes. |
| R6.5 | Switch service (wizard: new / existing-pull / existing-push / existing-merge) | Nothing syncs before explicit confirm; safety-backup of the scrapped side first (merge: both sides, `switch-merge`); password verified by decrypt before mutation; wizard never deletes local data; draft survives navigation (no secrets in draft), cleared on complete/start-over. |
| R6.6 | Unlink | Clears credentials ONLY; local items stay; sync stops. Requires explicit confirm. |
| R6.7 | Backup export/import/preview | Envelope R2.5; import = preview (count) → two-step confirm → safety-backup local → dirty mirror (auto-push/conflict, never pull-over). 50 MB stream cap, tmp cleanup. |
| R6.8 | Change password (`ChangePasswordAsync`) | Guards: current required, new required; current derived key MUST equal stored (else `InvalidCredentials`, zero mutations/downloads/queue). Then, in ONE engine gate hold (`SyncEngine.ChangeKeyAsync`): download the server copy (remote moved since the last sync → `DataOutOfSync`, zero mutations) → safety-backup of the REAL server copy → push re-encrypted under the new key on that remote base → store the new derived key ONLY after the PUT landed (a failed push keeps the old password working) → rotate safety master (R7.3, best-effort). Old password stops working everywhere. |
| R6.9 | Duplicates bulk delete (`DeleteBookmarksAsync`) | Safety backup (local, `duplicates-cleanup`) BEFORE delete; one transaction + one push; result toast names the backup location. Last-copy-locked plan (never wipe a group). |
|---|---|---|
| R6.10 | Conflict resolution (`ResolveConflictPullAsync` / `ResolveConflictPushAsync` / `ResolveConflictMergeAsync`) | Precondition: status = Conflict + linked sync. Preview: `GetConflictMergePreviewAsync` → counts + samples (localOnly/remoteOnly/titleConflicts/foldersCreated/separatorsAdopted + RemoteLastUpdated). UI presents 3 choices: Pull (take remote, backup local → `conflict-pull`), Push (upload local, backup remote → `conflict-push`), Merge (union both, backup both → `conflict-merge`). Safety backup of destroyed side(s) taken FIRST (fail closed). On success: conflict state cleared, status refreshed. |

### Merge rules (normative — port of KeepmarkApp `src/shared/merge.ts`)

`BookmarkMerge.Merge(local, remote)` is pure (no I/O, inputs never mutated).
The same function serves preview and apply, so preview counts MUST equal
the applied result. Rules:

1. Remote is the shared truth: its nodes, ids and metadata are kept as-is.
2. Local bookmarks whose `NormalizeUrl` URL is absent remotely are adopted
   with fresh ids (`maxRemoteId+1…`) into the folder matched by full title
   path, or into a newly created folder.
3. Same URL, different title → remote title wins, counted as `TitleConflicts`.
4. Separators (`xbs:separator`) are unioned from both sides, never matched.
5. Deletes are NOT merged (no tombstones in the protocol): items deleted on
   one side while the other was offline reappear after a merge.
6. `NormalizeUrl`: lowercase host, default ports + fragments dropped,
   tracking params (`utm_*`, `fbclid`, `gclid`, `msclkid`, `mc_cid/mc_eid`,
   `igshid`) removed; path + query stay case-sensitive. Unparsable input:
   trim + lowercase, fragment and trailing slash dropped.
7. `remoteOnly` is computed against the remote as received (not the merged
   tree); `localOnly` counts adopted local bookmarks at all levels.
8. Corrupt shape (bookmark carrying children): bookmarks kept, children
   flattened into the same folder instead of dropped.

## 7. Safety backups (summary — full spec in `docs/DEVELOPMENT.md` § Safety backups)

| R | Requirement |
|---|---|
| R7.1 | Obligation matrix (§1 of the DEVELOPMENT.md spec): every overwrite snapshots the DESTROYED side first (upload mode = server copy). Single Home delete keeps 6 s undo instead. No snapshot → operation must fail closed. |
| R7.2 | Identity: `safety-{ts}-{operation}[-n].json` + manifest `{FileName, CreatedAtUtc, Operation, Side, Items, SizeBytes, SyncId(truncated), Enc}`; legacy files listed as "Older backup"; retention newest 5 (+ manual delete, two-tap). |
| R7.3 | Encryption (envelope): files encrypted ONCE under random Master Key; record holds max 3 wraps `{KekId, WrappedKey}` (KEK = sync-derived key, id = SHA256 hex[..8]); rotation = single atomic write (tmp+fsync+rename, `.prev` kept until test-unwrap passes); rotation never fails the user flow. Pre-link/misaligned writes stay plaintext flagged "not encrypted". |
| R7.4 | Restore: local-side → pre-restore snapshot + replace local (dirty → push/conflict); remote-side → requires link + re-encrypt under CURRENT key + push (409 honest); never deletes the used entry; counts match `ValidateTree`; damaged → refuse. |
| R7.5 | Every destructive success message names the safety net + its location ("… — see Backup → Safety backups"). |

## 8. UI rules (pages in `src/KeepmarkMobile/Components/Pages/`)

| R | Requirement |
|---|---|
| R8.1 | Validation catalog (exact strings): "Current password is required.", "A new password is required.", "The new password must be different from the current one.", "New passwords do not match.", "Use a password of at least 8 characters.", "Passwords do not match — retype the confirmation.", "Enter both the sync ID and its password.", "Sync ID is required for an existing sync.", "Choose the sync direction above." (reconcile), "Verify the existing sync first.", "Re-check the target service first.", "Preview the merge first — counts must match the verified sync." (switch), "Preview the merge first — counts must match the linked sync." (connect) |
| R8.2 | Destructive confirms: unlink (1), delete server-side n/a (no client), create-empty (armed), import (two-step), switch (Review + Switch now), merge (Preview + explicit merge confirm, preview-locked), bulk delete (armed), safety restore/delete (armed inline). |
| R8.3 | Secrets hygiene: password fields cleared on success AND failure (keep-new convenience only where stated); password never in logs, toasts, or backup filenames. |
| R8.4 | Gating matrix: Switch-service menu + page require an existing link (disabled + guard page); Sync-now disabled unlinked; change-password section only when linked; reconcile direction defaults to take-remote (safe side first, other disabled when its side is empty; merge disabled unless both sides hold items); danger buttons outline-first/solid-confirm. |
| R8.5 | Status page: mode/state/last-sync/service (+version) / local DB bytes / server copy bytes + max + % + bar (one `/info` probe per visit, offline = "unknown") / content stats / app + sync-format + API versions. |
| R8.6 | About page declares v1.1 + v1.2 extended contract (with fallback) + encryption + independence + proprietary license (© Yari Melzani). Bug-report mail includes app/API versions. |
| R8.7 | Error evidence belongs to the step that produced it: wizard pages (Connect, Switch service) render one error box per action cluster (check / verify-link / apply-review) adjacent to its button, with `role="alert"`, instead of a single shared box. Multi-outcome successes restate WHAT ran (mode + direction + target) via the pure `MoveResult.Describe` helper (unit-pinned), never a bare "Done". |

## 9. Logging (`Logging.cs`, `InMemoryLogger`, Logs page)

R9.1 levels Error→Verbose, persisted verbosity, 200-entry viewer. R9.2 NEVER logged: bodies, passwords/keys, full sync IDs (truncate), DB password. R9.3 Network: START/END + ms + bytes at Info; timeouts/failures at Warn with type only. R9.4 Safety/master rotation events at Info without values.

## 10. Error catalog (`XbsErrors.cs` → UI)

401 SyncNotFound / 404 InvalidService / 405 NotAcceptingNewSyncs / 406 DailyNewSyncLimit / 409 DataOutOfSync (pull-first, never merge) / 413 SyncTooLarge / 429 Throttled (+Retry-After) / ServiceOffline (5xx/unreachable) / InvalidCredentials (decrypt fail) / UnsupportedSyncVersion / UnsupportedApiVersion / IncompleteSyncInfo / UnexpectedResponse. Each MUST surface its message verbatim (with optional `(server: …)` suffix), never swallowed, never shown as a different error.

## 11. Test matrix (what proves what)

| Suite | Proves |
|---|---|
| `ApiClientTests.cs` | routes, shapes, error mapping, timeouts |
| `CryptoInteropTests.cs` + `vectors.json` | R2.1–R2.2 both directions |
| `BackupGuardTests.cs` | R2.5 limits |
| `RetryPolicyTests.cs` | R3.3 backoff/idempotence |
| `StoreSyncTests.cs` | R4–R5 mirror/meta/engine flows |
| `BookmarkMergeTests.cs` | merge union rules, id freshness, folder paths, empty sides, tracking-param identity, corrupt flatten, `NormalizeUrl`, `MoveResult` 4 outcomes |
| `SafetyBackupTests.cs` | naming, describe, prune-5, legacy parse, index round-trip/damage, fingerprint, wrap trim |
| `VersionCompareTests.cs` | R1.11 |
| Manual/device — every view below MUST be exercised, none skipped | See per-view checklist. |

### Manual/device checklist (no view left unreviewed)

Each page in `src/KeepmarkMobile/Components/Pages/` maps to its requirements.
A future QA pass MUST walk every row; closing a view without its checks
fails the review.

| View | Checks |
|---|---|
| `Connect.razor` (R6.1–R6.4b, R8.1–R8.4, R8.7) | Check service ok/offline (error box by the button). Link existing: wrong password → `InvalidCredentials`, zero mutations; `0/0` → auto-apply. Direction pull/push/merge each applied once: success restates mode+direction+target (`MoveResult`), safety row appears. Merge: preview counts = applied result; change direction → preview invalidated, apply refuses; merge radio disabled when either side is empty. Create new + create-with-upload: new ID shown + Copy. Unlink: confirm armed, creds cleared, local kept. Each step's error appears in ITS box (`role="alert"`), never a shared one. |
| `SwitchService.razor` (R6.5, R8.1–R8.4, R8.7) | Unlinked → guard page. Step 1 check error by the button. Step 2 new-mode passwords mismatch/short → verify-box error (no navigation); existing-mode verify wrong password → zero mutations. All 3 directions reviewed in step 3 with counts; merge requires fresh preview. Execute → step 4 success names outcome + safety location; draft cleared (navigate away/back → wizard reset). Start over resets everything incl. errors. |
| `Backup.razor` (R6.7, R7) | Export → file created, envelope `{App:"xBrowserSyncNg",Format:1}`. Preview with wrong password vs damaged file → distinct errors. Import → two-step confirm, safety row first, count matches. Safety list shows all `connect-merge`/`switch-merge` rows (both sides). Restore local → pre-restore row; restore remote unlinked → refused; delete → file + index row gone. |
| `Duplicates.razor` (R6.9) | Groups listed; last copy locked (never deletable); per-group + global delete armed; ONE safety row per cleanup; ONE push (no sync storm — check logs); toast names backup location. |
| `Status.razor` (R8.5) | Mode/state/last-sync/service + version, DB bytes, server bytes + max + % + bar (one `/info` per visit; offline = "unknown"), content stats, app + sync-format + API versions. No `.ToString("0.#")` leaks into percents. |
| `Settings.razor` (R6.8) | Change password: empty current/new, new == current, wrong current → exact R8.1 strings, zero mutations; success → old password dead everywhere, safety row `change-password`, master re-wrapped. Theme/mode/verbosity persist. |
| `Home.razor` (R4.1, R4.4, R4.5) | Tap opens, ⋮ menu actions, swipe expand/collapse, pinned section, breadcrumb, folder-first sort, share-receive prefill, folder picker Select/Cancel. Edit in Auto → background push; conflicting remote → Conflict status, never silent merge/discard. Scroll/query preserved across `DataChanged`. Single delete → 6 s undo, no safety file. |
| `Logs.razor` (R9) | 200-entry viewer, level filter, share export. No bodies/passwords/keys/full sync IDs in any row (spot-check after a sync + a merge). |
| `About.razor` (R8.6) | Declares v1.1 + v1.2 extended contract with fallback, encryption, independence, proprietary license. Bug-report mail carries app + API versions. |
| Cross-view | R4.2 live v1.2 against a v1.2 server (conditional GET 304 + revision CAS in logs). R7.4 restore against real server. Uninstall/reinstall data-loss behavior. |

## 12. Static checks (reviewer-run, must all pass)

1. `grep -rn "SecureStorage.SetAsync" src/KeepmarkMobile --include=*.cs` → only derived keys + db key, never a variable named `*password*` (raw).
2. `grep -rniE "password.*log|Log.*password" src/Keepmark.Core --include=*.cs` → no hits (secrets in logs).
3. `grep -rn "Accept-Version" src/Keepmark.Core --include=*.cs` → `1.1.13` default sites + `1.2.0` ONLY in `GetInfoV12Async`, `GetBookmarksV12Async`, `UpdateBookmarksV12Async`, conditional GET.
4. `grep -rn "HttpMethod.Delete" src/Keepmark.Core` → no hits (no server delete in this client — by design).
5. `grep -rn "TODO\|FIXME\|HACK" src/ | tests/` → triage list, none load-bearing.
6. `dotnet test` green + both-direction crypto vectors green + `dotnet build … -f net10.0-android` 0 errors before any commit.

## 13. Development requirements (prevent regression)

### 13.1 Concurrency & gate discipline

- **Every `await _gate.WaitAsync()` MUST have a matching `_gate.Release()` in a `finally` block.** No exceptions. The `SyncQueue` runner wraps `runJob` in try/finally but individual engine methods that acquire the gate directly must release it.
- **Conflict resolution methods** (`ResolveConflictPullAsync`, `ResolveConflictPushAsync`, `ResolveConflictMergeAsync`) MUST:
  1. Set `_resolvingConflict = true` at start of `try`
  2. Reset `_resolvingConflict = false` in `finally` (after `_gate.Release()`)
  3. Never call other public sync methods that might re-acquire the gate (avoid deadlock)
- **`ForceSyncAsync` and `PushInBackgroundAsync`** MUST check `_resolvingConflict` **after** acquiring `_gate` and return early if true. This prevents false Conflict status during resolution.
- **Background timers** (poll, native debounce) MUST NOT fire sync logic while `_resolvingConflict` is true.

### 13.1b Push-base rule (review 2026-10-08)

- Every PUT that follows an explicit GET (conflict pull/push/merge, link
  upload/merge, switch, password change) uses the `lastUpdated` /
  `meta.revision` of THAT GET (`PushBase`), never the base stored in the
  mirror — the stored base is exactly what caused the conflict, and after a
  previous link it belongs to another sync.
- Link flows switch credentials only after the server accepted the write
  (`LinkUploadAsync`) or the remote tree decrypted and replaced the mirror
  (`LinkAdoptRemoteAsync`); a 409 / wrong password leaves the previous link
  intact.
- Conflict resolutions run inside `RunConflictResolutionAsync`: one gate
  hold, `_resolvingConflict` + timer suspension undone in a single `finally`
  even when the gate wait times out.
- Destructive conflict choices are armed (tap → inline confirm) and the
  success text names Backup → Safety backups (R7.5, R8.2).
- Regression suite: `ConflictResolutionTests.cs` (fake server enforces real
  base semantics: stale timestamp/revision → 409).

### 13.2 Merge retry discipline

- **Merge push MUST retry on 409** (DataOutOfSyncException) with re-fetch + re-merge (max 3 attempts). Single push attempt is insufficient when remote can change during merge.
- Re-merge uses the **same pure `BookmarkMerge.Merge` function** as preview, so counts always match.

### 13.3 State capture completeness

- **All conflict entry points** must capture conflict state: push (409), pull (if local dirty), auto-sync (both dirty). The conflict state must include: local tree, remoteLastUpdated, encrypted local blob, local count.
- **Preview API** (`GetConflictMergePreviewAsync`) MUST re-fetch remote at call time, not return cached state.

### 13.4 Test coverage requirements

- **Concurrency tests**: race between auto-sync and conflict resolution (Pull/Push/Merge)
- **Merge retry test**: simulate 409 on push, verify re-merge + retry succeeds
- **Gate release test**: every engine method acquiring `_gate` verified to release in `finally`
- **Flag test**: `_resolvingConflict` prevents ForceSyncAsync/background push during resolution

### 13.5 Release checklist (Keepmark Android)

- [ ] `dotnet test` green (131+ Core tests)
- [ ] `dotnet build -f net10.0-android` 0 errors
- [ ] Device deploy: `dotnet build -t:Run` or `adb install`
- [ ] Manual device QA: conflict → merge → safety backup visible
- [ ] Version bump in `KeepmarkMobile.csproj` (`ApplicationDisplayVersion`, `ApplicationVersion`)
