# Keepmark — Verification Specification (for outsourced code review)

This document states the REQUIRED behavior of the Keepmark Android app in
verifiable form. A third-party reviewer checks the code (and the running app)
against each numbered requirement. Where the code is known to violate a
requirement, it is marked **[KNOWN GAP]** instead of being silently adjusted.

Conventions: `Rxx` = requirement IDs. "Must" = fail the review if violated.
File paths are relative to `KeepmarkMobile/`. Commands run from `KeepmarkMobile/` with
`ANDROID_HOME=$HOME/android-sdk`, `JAVA_HOME=$HOME/jdk-21`
(JDK 21; system JDK is rejected). On Linux x64 first run
`python3 scripts/prepare-sqlcipher.py --linux` (prerequisites in DEVELOPMENT.md),
then `dotnet test` (xUnit, no device),
Android build `dotnet build src/KeepmarkMobile -f net10.0-android -c Debug`.

Global rules (verify everywhere): UI strings in en/it/fr/es/de/ja/pt/ru/zh/ro via `Loc`/`IXbsLocalizer` (log strings stay English); no telemetry;
HTTPS is the default and the only scheme in the curated catalog, while plain
`http://` is accepted only as the user's explicit choice and must be warned
about at the point of choice (R1.12, R8.8);
persisted state is typed (records/enums/`System.Text.Json` models), never an
untyped bag interpreted ad hoc; raw passwords exist only as method arguments,
never stored, never logged.

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
| R1.10 | `SyncPollInterval` / `ServiceProbeTimeout` / `DefaultHttpTimeout` | 15 min after completed foreground tick (Auto only) / 3 s / 12 s |
| R1.11 | `CompareVersions` | numeric dotted compare, tolerates leading `v` (`1.1.13` > `1.1.9`); unit-pinned in `VersionCompareTests.cs` |
| R1.12 | `PreferredServiceScheme` / `HttpScheme` (`Workflow/WorkflowRules.cs`) | `"https"` / `"http"`. Helpers: `IsCleartextServiceUrl`, `SplitServiceUrl`/`ComposeServiceUrl` (inverses; a bare host composes to `https://`), `NormalizeServiceUrl`. `ServiceSelector.Check`, `ConnectQrPayload.TryParse`, `ServiceCatalog.Remember` accept both; no code compares scheme literals. |
| R1.13 | Android network security (`Platforms/Android/NetworkSecurity/`) | `release.xml` sets `cleartextTrafficPermitted="true"` (Android cannot express address ranges); `debug.xml` permits cleartext only to `localhost`/`127.0.0.1` for `adb reverse`. A LAN HTTP service is therefore testable with a Release build, not Debug. |

## 2. Crypto (`src/Keepmark.Core/Crypto/`)

| R | Requirement | How to verify |
|---|---|---|
| R2.1 | Envelope = `base64(IV16B \|\| AES-GCM-256(lzutf8(JSON)))`; key = PBKDF2-SHA256/250k over UTF-8(password), salt = UTF-8(syncId) | `XCryptoService.cs:DeriveKey/Encrypt/Decrypt`; BouncyCastle required (16B IV — .NET `AesGcm` only does 12B) |
| R2.2 | Byte-interop BOTH directions with `lzutf8@0.6.0` + original JS client | `CryptoInteropTests.cs` + `TestData/vectors.json` green; never change `Crypto/` without running both Node directions |
| R2.3 | Legacy sync without version rejected ("open once in the official xBrowserSync client") | `DeriveKey` throws `UnsupportedSyncVersionException` on empty version |
| R2.4 | Decrypt failure (wrong password / corrupt) surfaces as `InvalidCredentialsException`, never as generic error | Code + wrong-password tests |
| R2.5 | Encrypted backup envelope `{App:"xBrowserSyncNg", Format:1, Salt:Base64(16B random), Blob}`; password ≥ 8, file ≤ 50 MiB, decoded envelope ≤ 50 MiB, decompressed UTF-8 ≤ 256 MiB, tree ≤ 200k nodes (`BackupGuard`) | `BackupGuardTests.cs`; damaged-file vs wrong-password distinguished (base64 pre-check) |

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
| R4.1 | Whole-blob only (API has no deltas). Mobile always uses Auto (foreground-only polling + one coalesced push worker with 250 ms grouping); Settings has no mode selector and ignores the legacy `sync_mode` preference. Explicit **Sync now** remains available. Core `SyncMode.Manual` remains an internal test API. Statuses: Idle/Syncing/Offline/Conflict/Error/NoCredentials + `DataChanged`/`StatusChanged` events; UI preserves scroll/query across events. |
| R4.2 | v1.2 capability probed via `/info` capabilities per service URL, cached; legacy → timestamp path. Conditional GET (304) skips download when hash known. Revision CAS on push when revision stored; 409 on races, never overwrite. |
| R4.3 | v1.2 capability probed on the 1.2.0 contract (`GetInfoV12Async` — the legacy contract strips capabilities, so probing there could never succeed), cached per service URL; 412 / missing capabilities = legacy. A service older than MinApiVersion throws instead of being silently used. Verifier: against a v1.2 server, conditional GET and revision CAS must be observed in logs. |
| R4.3b | `capabilities.syncExpiryDays` (v1.2 `/info`) is display only: integer ≥ 0 shown on Status (`0` = kept forever), anything else ignored. A malformed value MUST NOT fail the capability probe (`ApiClientTests.SyncExpiryDays_MalformedNeverBreaksTheProbe`). |
| R4.4 | Pull MUST refuse when the mirror is dirty (unpushed edits): status Conflict + `DataOutOfSyncException`, never silent discard. |
| R4.5 | `ForceSyncAsync` matrix: remote≠local + dirty → Conflict (no auto-merge); remote≠local + clean → pull; equal + dirty → push; equal + clean → stamp `LastSyncAt`. Offline → Offline status, no throw to UI. |
| R4.6 | **Conflict resolution**: when status is Conflict, engine exposes `ResolveConflictPullAsync`, `ResolveConflictPushAsync`, `ResolveConflictMergeAsync`, and `GetConflictMergePreviewAsync`. UI MUST present 3 choices (Pull/Push/Merge) with preview counts from `GetConflictMergePreviewAsync`. Safety backup of destroyed side(s) taken BEFORE resolution (R7.1). Pull replaces local with remote; Push overwrites remote with local; Merge unions both sides (never deletes). |
| R4.7 | Version gates: service < MinApiVersion refused at check; remote sync format > SyncClientVersion refused on pull/compare. |
| R4.8 | `Normalize` on push: empty description/tags → null, descriptions truncated to 300 chars (byte-compatible with the official xBrowserSync clients). |
| R4.8a | Every push includes Menu/Other/Toolbar (`ContainerNames.EnsureContainers`) and persists added roots locally. Ordinary sync changes affecting more than 10 nodes snapshot the replaced side first (`sync-pull` / `sync-push`), fail closed on snapshot failure; moves count as removal + addition. |
| R4.8b | Sync activation saves the current side and adopts the target under one engine gate hold; queued edits cannot interleave. Failed remote adoption restores the prior dirty flag with the tree/blob/timestamp/version/revision/hash. Browser mapping migration, stale targeted deletion confirmations and cross-sync duplicate selections are browser-specific review fixes, not mobile actions. |
| R4.9 | Bulk delete by stable NodeId = ONE transaction + ONE push (no sync storms). Single delete returns the subtree for 6 s undo. |
| R4.10 | **401 means "the service no longer has this sync"** (`SyncEngine.GoneCredentials`): the engine records the credentials that failed (`GoneSync(creds, dismissed)`), sets status `Error`, and performs NO unlink, retry, upload or snapshot. `DismissGoneSync()` hides it until an explicit retry or a different sync; any successful sync clears it; a 401 for another sync replaces the notice. `XbsSession.RetrySyncAsync` clears the dismissal and runs an explicit `ForceSyncAsync`. |

## 5. Credentials & local store

| R | Requirement |
|---|---|
| R5.1 | Android KeyStore-backed `SecureStorage` keys ONLY: `xbs_sync_id`, `xbs_service_url`, `xbs_derived_key` (base64, NEVER raw password), `xbs_sync_version`, `xbs_db_key` (64 hex, created once, kept on unlink), `xbs_saved_syncs` (JSON array of dormant rows: service URL, sync ID, derived key, optional label/version, key fingerprint, last-used time). `ClearAsync` removes the four active sync keys and the active row, keeps the DB key and the dormant rows. Raw sync passwords are never stored. |
| R5.2 | Non-secrets in `Preferences` (`XbsSettings.cs`): service URL, verbosity, app lock + timeout, theme, open mode, list sort. |
| R5.3 | SQLCipher mirror `Meta`/`Blob`/`Items` (+FTS5): `lastUpdated`, `syncVersion`, `syncRevision`, `syncHash`, `dirty` ("1"/"0"); `Blob.bookmarks` = last encrypted blob. `ReplaceAll` clears revision/hash on legacy pulls. |
| R5.4 | `SavedSyncs` / `SavedSyncRegistry` (pure model + storage): identity = canonical service URL + sync ID (the ID alone is not unique); label ≤ 40 chars, unique case-insensitively, empty clears it; order last used → label → sync ID; upsert refreshes last use and preserves unsupplied label/version/fingerprint; `Deserialize` never throws (malformed JSON → empty, rows without URL or ID dropped, duplicates collapsed to the most recent); `KeyId` = `SafetyMaster.Fingerprint(derivedKey)`. On the first start after the upgrade `SyncActiveAsync` registers the live credentials once and is a no-op afterwards (no schema change, no migration). |

## 6. Session flows (`src/KeepmarkMobile/Services/XbsSession.cs`)

Each flow states preconditions, mutations in order, and the REQUIRED user-visible outcome. Raw passwords are method args only, cleared in UI `finally`.

| R | Flow | Required behavior |
|---|---|---|
| R6.1 | Create empty (`CreateNewSyncAsync`) | Safety-backup local → POST → derive → store creds → replace mirror EMPTY. UI arms with explicit confirm. Message states the backup + where to find it (R7). |
| R6.2 | Create with upload (`CreateNewSyncWithUploadAsync`) | POST → derive → store creds → upload mirror. Nothing local deleted. Returns new ID for display/copy. |
| R6.3 | Link existing, pull (`ProbeLinkAsync` + `ApplyRemoteAsync`) | Password verified by decrypting remote BEFORE any overwrite (wrong → `InvalidCredentials`, zero mutations). Safety-backup local side. `0/0` items → auto-apply; else explicit Download/Upload/Merge choice with counts. |
| R6.4 | Link existing, push (`ApplyLocalAsync`) | Same verify-first; safety-backup remote side; PUT on remote base (409 honest). |
| R6.4b | Link existing, merge (`GetMergePreviewAsync` + `ApplyMergeAsync`, `connect-merge`) | Same verify-first. Preview is a dry-run of the same pure union (`BookmarkMerge`): local-only upload / remote-only download / title-conflict (server wins) / folders-created / separators counts. Preview is locked: changing direction or re-verifying invalidates it. Apply snapshots BOTH sides first (fail closed), then writes the union locally and pushes (409 honest, both snapshots intact). Merge never deletes. |
| R6.5 | Saved syncs and activation (Syncs page; `+ Add a sync` opens Connect) | One active sync owns the local tree; dormant rows keep the service URL, sync ID and derived key (platform store), never the long password, and the list is local to the device. Activate: explicit row `ConfirmBlock` first, then device unlock when app lock is set, read-only probe/decrypt before any write (`VerifySavedSyncAsync`), fail-closed save of the active side (inside `LinkAdoptRemoteAsync`, holding the engine gate through adoption) whose message names the real cause (`ErrSavedConflict` pending conflict, `ErrSavedOffline` no connection, `ErrSavedGone` sync deleted on the server, `ErrSavedNotSaved` other push/service failure), then adopt from the blob the probe already read (one download); a `switch-take-remote` backup is written only when this device was unlinked (a switch from another linked sync keeps none: that copy is on the server); the previous sync stays listed. Forget/Unlink confirm; Forget keeps bookmarks, the server copy, other rows and all backups, and only prunes dead safety-master wraps; Unlink removes the active row and keeps dormant rows. Adding while linked offers only an existing sync (manual or QR), upserts by canonical URL + sync ID, is fail-closed (pending conflict or failed push cancels), and snapshots only a server copy overwritten by upload (`upload-local`); download and merge keep no snapshot while switching from a linked sync. No server-side sync list; no delete-from-server. |
| R6.6 | Unlink | Clears credentials ONLY; local items stay; sync stops. Requires explicit confirm. |
| R6.7 | Backup export/import/preview | Envelope R2.5; import = preview (count and consequences) → Restore backup → safety-backup local → dirty mirror (auto-push/conflict, never pull-over). 50 MiB stream cap, tmp cleanup. |
| R6.8 | Change password (`ChangePasswordAsync`) | Guards: current required, new required; current derived key MUST equal stored (else `InvalidCredentials`, zero mutations/downloads/queue). Then, in ONE engine gate hold (`SyncEngine.ChangeKeyAsync`): download the server copy (remote moved since the last sync → `DataOutOfSync`, zero mutations) → safety-backup of the REAL server copy → push re-encrypted under the new key on that remote base → store the new derived key ONLY after the PUT landed (a failed push keeps the old password working) → rotate safety master (R7.3, best-effort). Old password stops working everywhere. |
| R6.9 | Duplicates bulk delete (`DeleteBookmarksAsync`) | Safety backup (local, `duplicates-cleanup`) BEFORE delete; one transaction + one push; result toast names the backup location. Last-copy-locked plan (never wipe a group). |
| R6.10 | Conflict resolution (`ResolveConflictPullAsync` / `ResolveConflictPushAsync` / `ResolveConflictMergeAsync`) | Precondition: status = Conflict + linked sync. Preview: `GetConflictMergePreviewAsync` → counts + samples (localOnly/remoteOnly/titleConflicts/foldersCreated/separatorsAdopted + RemoteLastUpdated). UI presents 3 choices: Pull (take remote, backup local → `conflict-pull`), Push (upload local, backup remote → `conflict-push`), Merge (union both, backup both → `conflict-merge`). Safety backup of destroyed side(s) taken FIRST (fail closed). On success: conflict state cleared, status refreshed. |
| R6.11 | Removed sync (401) notice (`SyncGoneNotice` on Home and Status; `XbsSession.UnlinkGoneSyncAsync` / `RetrySyncAsync`) | Exactly three actions: **Try again**, **Unlink and keep a copy…**, **Cancel**. Try again clears the dismissal and runs an explicit `ForceSyncAsync`; a repeated 401 keeps quiet. Cancel dismisses that sync until an explicit retry or a different sync. Nothing is unlinked, uploaded, snapshotted or retried automatically. Unlink-and-keep asks once, then (1) loads the local tree, validates it with `BackupGuard.ValidateTree` and counts items, (2) writes `removed-syncs/removed-<sha256(tree)>.json` = `{Format:1, CreatedAtUtc, ServiceUrl, Items, Tree}` encrypted with the device DB key (`xbs_db_key`, which survives an unlink), (3) decrypts and re-hashes to VERIFY (a failed check deletes the temp file and aborts), (4) unlinks and clears the 401 state. Zero local items → straight unlink, no empty file. |
| R6.12 | Removed-sync copies (Backup → **Copies of removed syncs**) | Listed with local time, service host and item count; damaged/foreign files are skipped, never listed or deleted. **Restore** (confirmed) first snapshots the current local tree (`pre-restore`), replaces the local tree and marks it dirty (pushed at the next sync while linked); while linked it is fail-closed (the active sync is saved first, otherwise `ErrRestoreActivePending` and nothing is replaced). A list that cannot be read says so (`ErrLoadFailed`) and logs a warning instead of looking empty. **Delete** (confirmed) removes only that file. Copies are content-addressed (identical trees stored once) and are NEVER pruned automatically. |

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
| R7.1 | Obligation matrix (DEVELOPMENT.md §11.1): every overwrite snapshots the DESTROYED side first (upload mode = server copy); Connect uses `take-remote` / `upload-local` / `connect-merge` / `create-empty`; Add-while-linked writes `upload-local` only (download and merge keep nothing), activation uses `switch-take-remote` only when it links an unlinked device, import `import-backup`, password change `change-password`, safety restore `pre-restore`, conflict `conflict-pull` / `conflict-push` / `conflict-merge`, duplicates `duplicates-cleanup`, selection delete `selection-delete`. Reversible steps write none: a single Home delete keeps its 6 s undo, a batch move and its undo restore in place. `switch-upload` / `switch-merge` / `selection-move` / `selection-move-undo` are legacy-only labels for old files. No snapshot → operation must fail closed. |
| R7.2 | Identity: `safety-{ts}-{operation}[-n].json` + manifest `{FileName, CreatedAtUtc, Operation, Side, Items, SizeBytes, SyncId(truncated), Enc}`; legacy files listed as "Older backup"; retention newest N from Settings (3/5/10/20/30/50, default 10; lowering it below the stored count deletes the oldest only after an inline confirmation) (+ manual delete, two-tap). |
| R7.3 | Encryption (envelope): files encrypted ONCE under random Master Key; record holds max 3 wraps `{KekId, WrappedKey}` (KEK = sync-derived key, id = SHA256 hex[..8]); rotation = single atomic write (tmp+fsync+rename, `.prev` kept until test-unwrap passes); rotation never fails the user flow. Pre-link/misaligned writes stay plaintext flagged "not encrypted". |
| R7.4 | Restore: local-side → pre-restore snapshot + replace local (dirty → push/conflict); remote-side → requires link + re-encrypt under CURRENT key + push (409 honest); never deletes the used entry; counts match `ValidateTree`; damaged → refuse. |
| R7.5 | Every destructive success message names the safety net + its location ("… — see Backup → Safety backups"). |

## 8. UI rules (pages in `src/KeepmarkMobile/Components/Pages/`)

| R | Requirement |
|---|---|
| R8.1 | Validation catalog (exact strings): "Current password is required.", "A new password is required.", "The new password must be different from the current one.", "New passwords do not match.", "Use a password of at least 8 characters.", "Passwords do not match — retype the confirmation.", "Enter both the sync ID and its password.", "Sync ID is required for an existing sync.", "Choose the sync direction above." (reconcile), "Verify the existing sync first.", "Re-check the target service first.", "Preview the merge first — counts must match the linked sync." (connect) |
| R8.2 | Destructive confirms (one inline `ConfirmBlock` next to the action, never a second confirmation after a review and never `window.confirm`): unlink this device, change password (armed), create-empty (armed), backup import (the verified review IS the confirmation), safety restore/delete, removed-sync copy restore/delete, add-an-existing-sync while linked (direction review applies directly), merge (preview-locked, applies directly), duplicates bulk delete, selection delete/move. The 401 notice asks once before unlinking and keeping a copy. There is no delete-from-server action anywhere. |
| R8.3 | Secrets hygiene: password fields cleared on success AND failure (keep-new convenience only where stated); password never in logs, toasts, or backup filenames. |
| R8.4 | Gating matrix: `+ Add a sync` appears only while linked and offers only the existing-sync path (a brand-new sync requires unlinking first, bookmarks stay); sync-now disabled unlinked; change-password section only when linked; reconcile direction defaults to take-remote (safe side first, other disabled when its side is empty; merge disabled unless both sides hold items); danger buttons outline-first/solid-confirm. |
| R8.5 | Status page (summary card with state word + dot and last sync, content tiles, Storage and Synchronization cards, app versions footer; no sync-mode line): state/last-sync/service (+version) / server sync expiry when the v1.2 service advertises it (`0` = kept forever, unknown = "—") / local DB bytes / server copy bytes + max + % + bar (one `/info` probe per visit, offline = "unknown") / content stats / app + sync-format + API versions; when the status is Conflict it owns the download/upload/merge resolution block with preview counts, and the 401 notice appears above it. |
| R8.6 | About page declares v1.1 + v1.2 extended contract (with fallback) + encryption + independence + proprietary license (© Yari Melzani). Bug-report mail includes app/API versions. |
| R8.7 | Error evidence belongs to the step that produced it: Connect renders one error surface per action cluster (check / verify-link / apply-review) adjacent to its action, with `role="alert"`, instead of a single shared box. The check cluster reports inside the service status card (see "Service status card (v1.0.210)"): a failed check turns the card red with `role="alert"` and the message verbatim; verify-link and apply-review keep their adjacent boxes. Multi-outcome successes restate WHAT ran (mode + direction + target) via the pure `MoveResult.Describe` helper (unit-pinned), never a bare "Done". |
| R8.8 | HTTP warning: choosing `http` in the custom-service editor must immediately show `Str.ServiceCleartext` next to the picker (`ServiceSelector`, icon + sentence, `role="status"`), stating that the sync ID and the encrypted bookmarks travel unprotected. The warning is not an error and does not block; it disappears when the URL returns to `https`. **[KNOWN GAP]** the QR review for an `http://` payload shows the URL without repeating this warning. |
| R8.9 | Syncs page: active row marked `Active`, dormant rows marked `Inactive` with Activate / Rename / Forget / Unlink. Activation is refused with an inline message when the row has no key (`ErrSavedNoKey`), when app lock is set and the unlock is cancelled, or when the sync is already active. The intro states that one sync is active at a time and that the sync you leave stays in the list. An unreadable list shows `ErrLoadFailed` (with a log warning) instead of the empty state; the rename field's placeholder is the name the row would get from its host. |
| R8.10 | Removed-sync notice: Home and Status show the same `SyncGoneNotice` (one notice, not two) with the three actions of R6.11; the body explains that the service no longer has this sync and that nothing on the device changed. **[KNOWN GAP]** the body hardcodes the common three-week expiry even when a v1.2 service advertises a different `syncExpiryDays`. |

## 9. Logging (`Logging.cs`, `InMemoryLogger`, Settings → Show logs)

R9.1 levels Error→Verbose, persisted verbosity, 200-entry viewer. R9.2 NEVER logged: bodies, passwords/keys, full sync IDs (truncate), DB password. R9.3 Network: START/END + ms + bytes at Info; timeouts/failures at Warn with type only. R9.4 Safety/master rotation events at Info without values.

## 10. Error catalog (`XbsErrors.cs` → UI)

401 SyncNotFound (raises the removed-sync notice of R4.10/R6.11, never a bare error) / 404 InvalidService / 405 NotAcceptingNewSyncs / 406 DailyNewSyncLimit / 409 DataOutOfSync (pull-first, never merge) / 413 SyncTooLarge / 429 Throttled (+Retry-After) / ServiceOffline (5xx/unreachable) / InvalidCredentials (decrypt fail) / UnsupportedSyncVersion / UnsupportedApiVersion / IncompleteSyncInfo / UnexpectedResponse. Each MUST surface its message verbatim (with optional `(server: …)` suffix), never swallowed, never shown as a different error.

## 11. Test matrix (what proves what)

`dotnet test` runs **397 tests** (xUnit, no device) after
`python3 scripts/prepare-sqlcipher.py --linux`; `node tests/js/gestures.test.cjs`
runs the gesture suite (node:test, no browser).

| Suite | Proves |
|---|---|
| `ApiClientTests.cs` | routes, shapes, version headers, error mapping, timeouts, expiry capability, malformed capabilities |
| `CryptoInteropTests.cs` + `vectors.json` | R2.1–R2.2 both directions |
| `BackupGuardTests.cs` | R2.5 limits, tree validation, password rules |
| `RetryPolicyTests.cs` | R3.3 backoff/idempotence |
| `StoreSyncTests.cs` | R4–R5 mirror/meta/engine flows, stable RowIds, restore |
| `BookmarkMergeTests.cs` | merge union rules, id freshness, folder paths, empty sides, tracking-param identity, corrupt flatten, `NormalizeUrl`, `MoveResult` outcomes |
| `ConflictResolutionTests.cs` | fake server with real base semantics, 409, merge retry, gate/flag discipline |
| `SafetyBackupTests.cs` | naming, describe, prune-10, legacy parse, index round-trip/damage, fingerprint, wrap trim |
| `SavedSyncsTests.cs` | R5.4 identity, canonical URL, labels, upsert, sort, defensive JSON |
| `SavedSyncActivationTests.cs` | read-only activation probe, fail-closed switch, previous row kept |
| `RemovedSyncNoticeTests.cs` | R4.10/R6.11 notice state, dismiss/retry, copy-before-unlink, dedupe |
| `ServiceCatalogTests.cs` | catalog, normalization, saved metadata, stale checks, late generations |
| `ConnectQrTests.cs` | strict parser, bounded sizes, candidate generations/timeout/consume |
| `WorkflowTests.cs` | URL split/compose, password rules, short IDs, cleartext detection |
| `I18nTests.cs` | ten-language parity and `Str` coverage |
| `RelativeTimeTests.cs` | relative time formatting |
| `SelectionBatchTests.cs`, `SelectionControllerTests.cs` | batch validation, undo, version gates |
| `ForegroundPollerTests.cs`, `LockPolicyTests.cs` | lifecycle/cancellation, relock presets |
| `VersionCompareTests.cs` | R1.11 |
| `StorePerformanceTests.cs`, `SyncPerformanceTests.cs` | query plans, targeted paths, aggregation, allocations |
| Manual/device — every view below MUST be exercised, none skipped | See per-view checklist. |

### Manual/device checklist (no view left unreviewed)

Each page in `src/KeepmarkMobile/Components/Pages/` maps to its requirements.
A future QA pass MUST walk every row; closing a view without its checks
fails the review.

| View | Checks |
|---|---|
| `Connect.razor` (R6.1–R6.4b, R8.1–R8.4, R8.7, R8.8) | Check service ok/offline (status card by the selector; a failed check is red with the message). Custom URL: switching the scheme to `http` shows the amber cleartext sentence immediately and it disappears on `https`; a host typed alone composes to `https://`. Link existing: wrong password → `InvalidCredentials`, zero mutations; `0/0` → auto-apply. Direction pull/push/merge each applied once: success restates mode+direction+target (`MoveResult`), safety row appears. Merge: preview counts = applied result; change direction → preview invalidated and recomputed automatically for Merge; apply waits for a current preview; merge radio disabled when either side is empty. Create new + create-with-upload: full new ID shown + Copy sync ID. While linked, `+ Add a sync` offers only the existing path and applies through the same review; the previous sync stays in Syncs. Unlink: confirm armed, creds cleared, local kept. Each step's error appears in ITS surface (`role="alert"`), never a shared one. |
| `Syncs.razor` (R6.5, R8.9) | Active row marked, dormant rows marked inactive; every row shows primary Activate (disabled on the active row), and ⋮ offers Rename then a separated Forget/Unlink group; the active row also offers `Change password` (a row task, not a Settings section, and no sync-lock prompt: the saved key is re-wrapped). Change password: empty current/new, new == current, wrong current → exact R8.1 strings, zero mutations; success → old password dead everywhere, safety row `change-password`, master re-wrapped; the action is armed. Only one row task open at a time. Activate asks device unlock when app lock is set (cancel → inline error, no write), verifies read-only (no writes on failure), then adopts the target (a `switch-take-remote` row only when the device was unlinked); the previous sync stays listed. Forget confirms, keeps bookmarks and the server copy, drops the row and prunes only dead safety-master wraps. Unlink this device confirms, unlinks, removes the active row and keeps dormant rows. Re-adding the same canonical URL + sync ID updates the row in place, never a duplicate; a row without a key reports `ErrSavedNoKey`. |
| `MoveServiceLegacy.razor` (legacy routes) | `/connect`, `/switch-service` and `/migrate` redirect to `/syncs`: old links, bookmarks and launcher shortcuts never hit a dead route. There is no multi-step switch wizard and no resume draft. |
| `Backup.razor` (R6.7, R6.12, R7) | Export → file created, envelope `{App:"xBrowserSyncNg",Format:1}`; the file name carries the slugged active sync name (`xbs-backup-<sync>-YYYYMMDD-HHmmss.xbs.json`). Preview with wrong password vs damaged file → distinct errors. Import → preview and consequences → Restore backup executes directly, safety row first, count matches. Safety list shows every operation tag (including `connect-merge` rows for both sides and legacy `switch-*` rows). Restore local → pre-restore row; restore remote unlinked → refused; delete → file + index row gone. Copies of removed syncs: listed with time/host/count, restore asks first and creates `pre-restore`, delete asks first and removes only that file, a damaged file is skipped and never deleted. |
| `Duplicates.razor` (R6.9) | Groups listed; last copy locked (never deletable); per-group + global delete armed; ONE safety row per cleanup; ONE push (no sync storm — check logs); toast names backup location. |
| `Status.razor` (R8.5) | Summary card (state word + dot, last sync), content tiles, then state/service + version, sync expiry when advertised, DB bytes, server bytes + max + % + bar (one `/info` per visit; offline = "unknown"), content stats, app + sync-format + API versions. In Conflict, the resolution block shows preview counts and applies one of download/upload/merge; the 401 notice appears above it. No `.ToString("0.#")` leaks into percents. |
| `Settings.razor` (R6.8) | Preferences only: theme/mode/verbosity/language persist. Logs: one `Show logs` button (`aria-expanded`, `aria-controls`) opens the panel with the verbosity select and the live log; `Hide logs` closes it; it is closed on entry. No Change password here (it belongs to the sync row). |
| `Home.razor` (R4.1, R4.4, R4.5, R4.10) | Tap opens, ⋮ menu actions, swipe expand/collapse, pinned section, breadcrumb, folder-first sort, share-receive prefill (also with ⋮ actions, edit, move picker or delete confirmation already open: only the new prefilled add form remains; repeat sharing the same URL resets the form), folder picker Select/Cancel. Edit in Auto → background push; conflicting remote → Conflict status, never silent merge/discard. Scroll/query preserved across `DataChanged`. Single delete → 6 s undo, no safety file. After a 401 the removed-sync notice offers Try again / Unlink and keep a copy… / Cancel; Cancel hides it, Try again returns it on a repeated 401. |
| `LogsPanel.razor` (R9, inside Settings) | 200-entry viewer, level filter, share export. No bodies/passwords/keys/full sync IDs in any row (spot-check after a sync + a merge). |
| `About.razor` (R8.6) | Declares v1.1 + v1.2 extended contract with fallback, encryption, independence, proprietary license. Bug-report mail carries app + API versions. |
| Cross-view | R4.2 live v1.2 against a v1.2 server (conditional GET 304 + revision CAS in logs). R7.4 restore against real server. A plain-HTTP self-hosted service: works in a **Release** build, refused/unreachable in Debug except loopback via `adb reverse`. Uninstall/reinstall data-loss behavior (removed-sync copies live in app-private storage, so uninstall deletes them — the encrypted export is the portable backup). |

## 12. Static checks (reviewer-run, must all pass)

1. `grep -rn "SecureStorage.SetAsync" src/KeepmarkMobile --include=*.cs` → only derived keys, the db key and the saved-sync registry, never a variable named `*password*` (raw).
2. `grep -rniE "password.*log|Log.*password" src/Keepmark.Core --include=*.cs` → no hits (secrets in logs).
3. `grep -rn "Accept-Version" src/Keepmark.Core --include=*.cs` → `1.1.13` default sites + `1.2.0` ONLY in `GetInfoV12Async`, `GetBookmarksV12Async`, `UpdateBookmarksV12Async`, conditional GET.
4. `grep -rn "HttpMethod.Delete" src/Keepmark.Core` → no hits (no server delete in this client — by design).
5. `grep -rn '"http"\|"https"' src/Keepmark.Core src/KeepmarkMobile --include=*.cs --include=*.razor` → only in `WorkflowRules` (constants) and the scheme picker markup; no ad-hoc scheme comparisons elsewhere.
6. `grep -rn "TODO\|FIXME\|HACK" src/ | tests/` → triage list, none load-bearing.
7. `dotnet test` green + both-direction crypto vectors green + `dotnet build … -f net10.0-android` 0 errors before any commit.

## 13. Development requirements (prevent regression)

### 13.1 Concurrency & gate discipline

- **Every `await _gate.WaitAsync()` MUST have a matching `_gate.Release()` in a `finally` block.** No exceptions. The `SyncQueue` runner wraps `runJob` in try/finally but individual engine methods that acquire the gate directly must release it.
- **Conflict resolution methods** (`ResolveConflictPullAsync`, `ResolveConflictPushAsync`, `ResolveConflictMergeAsync`) MUST:
  1. Set `_resolvingConflict = true` at start of `try`
  2. Reset `_resolvingConflict = false` in `finally` (after `_gate.Release()`)
  3. Never call other public sync methods that might re-acquire the gate (avoid deadlock)
- **`ForceSyncAsync` and `PushInBackgroundAsync`** MUST check `_resolvingConflict` **after** acquiring `_gate` and return early if true. This prevents false Conflict status during resolution.
- **Background timers** MUST NOT fire sync logic while `_resolvingConflict` is true.
- **Foreground lifecycle**: no periodic poll or Home clock while stopped. Rapid
  stop/resume never creates overlapping workers; cancellation/disposal drains work.
- **Push acknowledgement**: ordinary push preserves RowIds and FTS; normalizes only
  changed descriptions; blob/revision/hash/dirty are updated transactionally. An
  edit arriving during upload is never lost. Redundant queued pushes skip clean data.
- **UI lifetime**: dispose during initialization cannot create later timers or
  event subscriptions. A late gesture release cannot detach a newer component.
- **Resource bounds**: HTTP JSON ≤72 MiB, error body ≤64 KiB, encrypted envelope
  ≤50 MiB, decompressed UTF-8 ≤256 MiB; no device limit is mislabeled as a server limit.

### 13.1b Push-base rule (review 2026-10-08)

- Every PUT that follows an explicit GET (conflict pull/push/merge, link
  upload/merge, activation, add while linked, password change) uses the
  `lastUpdated` / `meta.revision` of THAT GET (`PushBase`), never the base
  stored in the mirror — the stored base is exactly what caused the conflict,
  and after a previous link it belongs to another sync.
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

- [ ] `dotnet test` green (390 Core tests)
- [ ] `dotnet build src/KeepmarkMobile -f net10.0-android -c Debug` 0 errors
- [ ] Device deploy via `dotnet build -t:Run` (a Debug APK installed with plain
      `adb install` aborts: “No assemblies found … Fast Deployment”)
- [ ] Manual device QA: conflict → merge → safety backup visible; activate a
      second saved sync and reactivate the first; force a 401 (delete the sync
      on the server) → notice → unlink and keep a copy → restore the copy
- [ ] Version bump in `KeepmarkMobile.csproj` (`ApplicationDisplayVersion` 1.0.N,
      `ApplicationVersion` M) plus the `?v=` cache-buster in `wwwroot/index.html`
      when CSS/JS changed; commit as `v1.0.N (M): …`
- [ ] Play upload: signed AAB — `scripts/build-play-aab.sh` (asks the keystore
      password; `KEEPMARK_KEY_PASS`/`KEEPMARK_KEY_PASS_FILE` for unattended runs)
      → `src/KeepmarkMobile/bin/Release/net10.0-android/com.keepmark.mobile-Signed.aab`
- [ ] Verify the artifact before uploading: `keytool -printcert -jarfile …aab`
      (upload key fingerprint) and `adb -s IP:PORT shell "dumpsys package
      com.keepmark.mobile | grep -m1 versionName"` after installing the test APK


## Native database engine verification

- Load SQLCipher 4.19.0 / SQLite 3.53.4 through the sqlcipher provider; refuse
  startup when `cipher_version` is absent. Never fall back to plaintext SQLite.
- Preserve the original encrypted fixture and database key/format. Verify
  old/new engine reads and writes in both directions, including FTS.
- Final APK must contain the pinned official arm64-v8a and x86_64 libraries,
  stored uncompressed with 16 KiB ELF and ZIP alignment. Run
  `python3 scripts/verify-native-sqlcipher.py --apk /path/to/app.apk`.
- Bundle SQLCipher and crypto third-party notices. Device runtime and battery
  validation remain separate from host tests and APK inspection.

### QR connection regression (v1.0.200)

- Pure parser: strict Keepmark JSON v1, Unicode and short existing passwords,
  1,800-byte cap before parsing, duplicates/depth/type/ID/URL/version rejection,
  unknown fields ignored, absolute `https`/`http` URL without credentials,
  query, fragment, backslash or control characters, representation redacted
  (`ConnectQrTests`). Candidate tests use a fake clock and delayed scanner:
  59/60 seconds, duplicate/late generations, cancellation and
  consume-before-probe (including failed probe).
- Connect (first link and `+ Add a sync` while linked → Existing sync) shows a
  QR icon with scan action and translated helper before service/ID/password
  entry. Adding while linked keeps the usual verification, direction and apply.
- QR review is exclusive in Connect: only service URL, abbreviated
  sync ID, acquired-credentials summary and spaced actions. Manual inputs,
  service controls and later Connect headings stay hidden while pending and
  verifying. `BeginVerification` consumes the secret but keeps `ShowReview`
  active with localized progress; no manual form flashes before the probe
  succeeds. Failure clears the candidate and shows the adjacent error.
- Scan alone performs no probe/settings/credential/bookmark writes; service
  and abbreviated ID are reviewed before Connect. The native scanner requests
  CAMERA on demand, decodes only QR and releases its handler in every exit.
- QR Connect enters the existing ProbeLink/reconcile/apply path; no bypass of
  preview, direction consequences or safety backups. Both-empty auto-apply is
  unchanged. Manual retry preserves its original behavior.
- Device QA required: denied/revoked permission, Back, Home/resume, rotation,
  biometric lock, repeated callbacks, Unicode QR and camera indicator off,
  with a Release APK as well as Debug. No connected device was available during
  implementation, so these hardware cases are not recorded as passed.
- **[KNOWN GAP]** an `http://` payload is accepted but the review does not
  repeat the cleartext warning (R8.8).


## Multiple selection — v1.0.201

- Long press selects once with native haptic feedback; ⋮ still opens single-item actions.
- Select All means current logical candidates, not only DOM rows or unseen search results.
- Folder/descendant overlap normalizes to roots, with covered descendant state.
- Selection disables row opening/swipes and context navigation; Back restores the list.
- Move picker supports immediate folder creation with explicit parent and final Move;
  draft blocks Move, cancel preserves created folders. The intentional create advances
  the selection version only through the gated result; external changes invalidate it.
- Move/delete uses strict NodeId + content version, one atomic mutation, one notification,
  verified safety backup before mutation, and no-op skips backup/notification.
- Delete confirmation separates selected roots from descendants. Backup recovery only;
  no global restore undo. Move undo restores original positions or refuses atomically.
- Background/lock closes interaction, cancels before mutation when possible, and never
  reverses a completed local commit. Pending shares do not open over selection/results.

Selection feedback regression: long press a top-level system container → immediate
informational toast, selection stays inactive; opening the folder afterward must
not reveal a stale red selection error below the list.

Move recents regression: complete a single or batch move to a folder, reopen
either picker → destination is first in Recent (also after restarting). Failed
or no-op moves do not update history. Deleted/forbidden folders and destinations
containing all selected items are omitted. Tap a recent chip → direct move through
the usual validated flow; Undo still works. No zero “already here” summary or
duplicate count below the tree; successful batch move shows nonzero item counts
and destination.

Move-picker spacing regression: check single/batch, no destination, partially
already-here, long bookmark title, deep destination path and new-folder form at
320/360/412px widths, both themes, 100%/140% text scale and reduced height
(keyboard/landscape).
Cancel and Move must stay visible; the name field uses a full row with Create
and Cancel beneath it. Recent/tree/search controls pause during a folder-name
draft; cancel the draft to resume destination selection. Long paths can scroll
within their footer area without displacing the actions. Original tree styling
is retained with the approved “Cartella incisa” outlined closed-pocket icon.

2026-10-17: actual MovePicker/RowTile compiled and rendered in an isolated
ASP.NET HtmlRenderer host (no Android workload required). Thirteen focused
interaction checks passed; headless Chromium checked 144 layout combinations
(six scenarios × three widths × two heights × two themes × two text scales),
with no horizontal overflow or hidden footer actions. This is layout/component
evidence; native keyboard, TalkBack and device behavior still require Android QA.

## Service selection — v1.0.208/209

- Connect uses `ServiceSelector`: a button with
  the current service, then the eight public services, the saved ones and
  “Custom URL…”. Each row shows the host (or the saved URL) with country, size
  limit and API version; the selected row is marked for assistive technology
  (`aria-pressed`) and the whole picker stays usable at large text scales.
- Only the latest selection may pass: `Workflow/ServiceCheckState.cs` ties
  readiness and create permission to a generation, so a probe that answers after
  the user switched endpoints leaves the action blocked and reports nothing about
  the abandoned service (`ServiceCatalogTests.LateResultCannotEnableAnotherSelectedService`).
  A service accepting existing syncs only (`NoNewSyncs`) is announced and blocks
  new-sync creation while still allowing an existing link.
- Custom URLs verify manually and the proof is invalidated by any edit; the URL
  is remembered only after a successful connect/create/add-while-linked, with the verified
  location, limit and API version refreshed only by later checks of that same URL.
  Legacy saved rows show unknown metadata until their next check.
  `ServiceCatalog.Normalize` ignores host case and default ports, keeps path case
  and rejects credentials/query/fragment, so equivalent addresses cannot create a
  second entry (`CanonicalServiceIdentityIgnoresHostCaseAndDefaultPort`).
- Connect opens on the service this device is linked to; the catalog
  default applies while no sync is linked. The Connect review and the confirm that
  precedes the apply name the destination with its full normalized URL, matching
  the completion message. The endpoint check result is the status card of
  "Service status card (v1.0.210)": dot + state word, endpoint chips (API, size,
  location; version and location dropped when the service omits them) and the
  server note.

Regression check on a device: link a custom HTTPS service, switch to a public one
and back; the saved row keeps country/limit/API, a service in `NoNewSyncs` blocks
only “Create a new sync”, and switching while a check is in flight never unlocks
the other endpoint.

## Connect mode choice — v1.0.210

- Connect offers two cards: `Link an existing sync` and `Create a new sync`.
  While linked, `+ Add a sync` offers only the existing-sync path, so a new sync
  is always created from an unlinked state.
- The retired Switch service segmented control (`SwIntoWhich`, `SwANewText` /
  `SwExistingText`) is gone with its page, nav item, strings and resume draft:
  `/connect`, `/switch-service` and `/migrate` redirect to `/syncs`.

Regression check on a device: while linked, `+ Add a sync` shows no “new sync”
option and a scan still asks for review before any network write; after creating
a new sync while unlinked, the full sync ID is shown with `Copy sync ID` and the
safety backup note. The status card shows the state word, the translated location
and the server message; the status page reports the same run (build 211). Native
TalkBack, rotation and font scaling still require device QA.

## Multiple saved syncs, removed syncs and cleartext — v1.0.211

- `xbs_saved_syncs` holds dormant rows only (service URL, sync ID, derived key,
  optional label/version, key fingerprint, last-used time). The active sync
  keeps the historical keys, so no data migration and no schema change. R5.4,
  R6.5.
- Activate never asks for the sync password again: it proves the device (app
  lock when set), probes/decrypts read-only with the stored key, saves the
  active side fail-closed and only then adopts the target; it snapshots the
  destroyed local tree (`switch-take-remote`) only when this device was
  unlinked, because a linked sync's copy is already on its server. A wrong key, expired
  sync, offline service or pending conflict leaves everything untouched.
- `+ Add a sync` while linked offers only the existing-sync path and writes the
  `upload-local` only when overwriting the target server copy; download and
  merge keep no snapshot when switching from a linked sync. Creating a new
  sync requires unlinking first; bookmarks stay.
- Forget prunes only safety-master wraps whose key no saved sync (active
  included) still holds, never the last wrap and never a backup file.
- The registry is read-modify-write and its writes are serialized, so the
  startup migration and a concurrent link/rename never drop a row. The
  migration itself is best-effort: a storage failure is logged, the app starts.
- Label uniqueness compares the displayed names (host fallback included) and
  the rename field shows the suggested name as its placeholder. The saved-sync
  list surfaces a read failure (`ErrLoadFailed`) instead of rendering the
  empty state.
- Unlinking puts the engine straight into `NoCredentials` instead of waiting
  for the next poll tick to notice the missing credentials.
- A 401 surfaces `SyncGoneNotice` on Home and Status with Try again / Unlink and
  keep a copy… / Cancel. Unlink-and-keep writes a verified, content-addressed
  `removed-<sha256>.json` under the device key before unlinking; copies are
  listed in Backup, restorable behind `pre-restore`, deletable only on request,
  never pruned automatically (R4.10, R6.11, R6.12).
- Plain `http://` is a deliberate, warned choice for self-hosted services:
  scheme picker in the custom editor, `Str.ServiceCleartext` sentence next to
  it, `http` allowed by the parser and version check, Release network security
  permitting cleartext, Debug limited to loopback (R1.12, R1.13, R8.8). No
  code compares scheme literals outside `WorkflowRules`.

Regression check on a device: add a second sync, activate back and forth (the
safety list keeps opening), forget a row (bookmarks and server copy survive),
delete a sync on the server and confirm the 401 notice keeps a copy before
unlinking, then restore that copy. Build a Release APK to test a LAN `http`
service with the amber warning visible; Debug only reaches loopback through
`adb reverse`. Native TalkBack, rotation, font scaling and camera cases still
require device QA.

Settings layout: the complete log block is the final functionality, immediately
before Save; all other preference blocks precede it.
