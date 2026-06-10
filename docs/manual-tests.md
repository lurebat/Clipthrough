# Manual Test Checklist

Verification steps that require a **real environment** and cannot be exercised by the
automated suite (`dotnet test`): real Windows DPAPI across users/machines, live
clipboard + global hotkeys, real network binds, the auto-update flow, and actual UI
responsiveness. Run the relevant section before release or when touching that area.

Legend: `[ ]` not run · `[x]` passed · `[!]` failed (file an issue).

Automated coverage for each area lives in `Clipthrough.Tests/`; this file is **only**
for what those tests cannot reach. Each entry says why it can't be automated.

---

## Phase 1 — Secrets at rest (R1)

Automated: `WindowsDataProtectionServiceTests` (real DPAPI round-trip + tamper),
`StorageOptionsServiceTests`, `SettingsServiceSecretsTests` (protect/migrate/in-memory
via fakes). Not automatable below.

### MT-1.1 — DB password persists across restart (real DPAPI)
*Why manual:* depends on the real per-user DPAPI key + app lifecycle.
1. Fresh install. Create an encrypted DB, enable "remember password".
2. Fully exit Clipthrough; relaunch.
- **Expect:** the app unlocks the DB automatically without prompting for the password.
- [ ]

### MT-1.2 — Protected key is NOT portable to another user/machine
*Why manual:* DPAPI CurrentUser scope can only be proven across real user contexts.
1. With MT-1.1 set up, copy `%LOCALAPPDATA%\Clipthrough\storage.json` (and the DB) to a
   **different Windows user account** (or machine).
2. Launch Clipthrough there pointing at the copied files.
- **Expect:** the protected password fails to unprotect; the app prompts for the password
  instead of silently unlocking. The clipboard DB is not readable with the copied config alone.
- [ ]

### MT-1.3 — Upgrade migration from a real prior (plaintext) install
*Why manual:* needs an actual pre-hardening `storage.json`/`settings.json` on disk.
1. Take a `storage.json` from the previous release (plaintext `databasePassword`) and a
   `settings.json` with a plaintext `aiApiKey`/`remoteApiToken` (remote API enabled).
2. Launch the hardened build over them.
- **Expect:** the app opens normally (no lockout). After launch, `storage.json` contains
  `databasePasswordProtected` and **no** plaintext `databasePassword`; `settings.json`
  contains neither secret (moved to `settings-ai-key.bin` / `settings-remote-token.bin`).
- [ ]

### MT-1.4 — Secrets are in-memory only when no real protector exists
*Why manual:* requires a non-Windows run (no DPAPI) — `NoOpDataProtectionService`.
1. Run on Linux/macOS (or force the no-op protector). Set an AI key / remote token and a DB password.
2. Inspect the config dir; then restart.
- **Expect:** no `*-ai-key.bin` / `*-remote-token.bin` sidecars and no `databasePasswordProtected`
  on disk; the secrets work for the session but are gone after restart (re-prompt). No plaintext anywhere.
- [ ]

---

## Phase 3 — SQLite concurrency (R6)

Automated: `SqliteConcurrencyTests` (in-process parallel writes, no lock errors;
`busy_timeout` applied on open). Not automatable below.

### MT-3.1 — No UI freeze on hotkey actions under writer contention
*Why manual:* "the UI did not freeze" is a real-time perception, not a headless assertion.
1. Build a large history (10k+ clips) with semantic search + OCR enabled so the background
   workers are actively writing.
2. Rapidly trigger the global hotkeys (copy-and-favorite, paste-and-delete, paste-at-offset)
   while the workers run.
- **Expect:** the window stays responsive; no multi-second freeze; actions complete. (Pre-fix,
  these blocked the UI thread for up to the 5s `busy_timeout`.)
- [ ]

### MT-3.2 — Sustained mixed load stays stable
*Why manual:* needs real ONNX embedding + Windows OCR + live capture over time.
1. Enable semantic search + OCR. Paste rapidly (text + images) for several minutes while
   searching and scrolling.
- **Expect:** no `database is locked` / `database table is locked` errors in the session log;
  captures are not dropped; CPU returns to idle when activity stops.
- [ ]

---

## Phase 4 — Remote API (R3)

Automated: `RemoteControlServiceTests` (real Kestrel on loopback: `/transform` removed,
sensitive redaction, 401 matrix, loopback-only `ResolveBindAddress`, docs require auth).
Not automatable below.

### MT-4.1 — Non-loopback bind is refused on a real network
*Why manual:* needs a real second host on the LAN.
1. Configure `RemoteApiBindAddress` to `0.0.0.0` (or the machine's LAN IP) and enable the API.
2. From another machine on the LAN, try to reach `http://<machine-ip>:<port>/clips` with the token.
- **Expect:** the server is **not** reachable off-host (it bound to loopback regardless); only
  `127.0.0.1` works. A trace warning notes the downgrade.
- [ ]

### MT-4.2 — Existing remote `script`/`ai` clients get a hard failure (breaking change)
*Why manual:* validates the intended break against a real external client.
1. Point any previously-working remote automation that POSTs `/clips/{id}/transform` (kind
   `script` or `ai`) at the new build.
- **Expect:** `404` (endpoint removed); no code executes. Confirm this is acceptable for your
  integrations and noted in release notes.
- [ ]

### MT-4.3 — Auth-failure backoff
*Why manual:* timing-dependent; deferred from unit tests (no injectable clock).
1. Send 6+ requests with a wrong bearer token within ~10 minutes from one IP.
- **Expect:** after 5 failures the responses are delayed ~1s; a subsequent **correct** token
  clears the penalty and responds promptly.
- [ ]

## Phase 2 — Storage-lifecycle crash-safety (R2)

Automated: `StorageOptionsServicePhase2Tests` (atomic rekey, same-path password validation,
path-move atomicity, no temp leaks), `DatabaseBackupServiceTests` (WAL-resident rows, restore
validation, prune). Not automatable below.

### MT-2.1 — Cross-volume database move
*Why manual:* cross-volume `File.Move` atomicity needs two physical drives/volumes.
1. With the DB on `C:\`, change the DB location (Settings -> Database Location) to a different
   drive (e.g. `D:\clips.db`). 2. Kill the process mid-copy (after the `.moving-*` temp appears,
   before the rename).
- **Expect:** on next launch the source DB is intact, the `.moving-*` temp is gone, and the
  destination is empty/absent — no data loss or corrupt file at either path.
- [ ]

### MT-2.2 — Restore under disk-full
*Why manual:* a disk-full condition can't be reliably simulated in `dotnet test`.
1. Fill the target disk near capacity. 2. Restore from a backup.
- **Expect:** failure is logged (InvalidOperationException), the `.before-restore-*` files are
  intact for manual recovery, and the app does not crash.
- [ ]

### MT-2.3 — Rekey / restore with the DB open in another process
*Why manual:* holding a handle from a separate OS process isn't reachable in-process.
1. Open the DB in the `sqlite3` CLI. 2. Trigger a rekey or restore from the app.
- **Expect:** the operation waits for/release-retries, or fails cleanly — the live DB is never
  left torn; `ClearAllPools` in the maintenance scope releases the app's own handles first.
- [ ]

---

## Phases 5 & 6 — Search scalability + worker robustness (R4, R5)

Automated: `ClipStoreServiceTests` (list/search omit BLOBs, field parity, Limit+1 overcount),
`SemanticSearchServiceTests` (race-free query, incremental append), `EmbeddingWorkerTests`
(inference-failure idles, missing-model idles). Not automatable below.

### MT-5.1 — Large image history stays memory-bounded
*Why manual:* needs 10,000+ multi-MB image clips and OS-level RSS measurement.
1. Accumulate 10,000+ image clips. 2. Scroll the list and run a text search while watching RSS.
- **Expect:** RSS stays proportional to text metadata, not the image corpus; scrolling/search
  does not load multi-MB BLOBs per visible row.
- [ ]

### MT-6.1 — Missing ONNX model does not pin the CPU
*Why manual:* requires renaming the real model at runtime to trigger the real-service failure.
1. Start with a valid ONNX model + semantic search on. 2. Rename the model file while running.
   3. Wait ~60s and watch CPU.
- **Expect:** embedding-worker CPU drops to near zero (idles); clips are NOT marked `failed`;
  replacing the model + "Re-run all" resumes embedding.
- [ ]

### MT-6.2 — Incremental embedding cache (no per-batch full reload)
*Why manual:* timing of O(M) vs O(N) cache loads is too noisy for `dotnet test`.
1. Build a 1,000+ clip embedding cache. 2. Copy a new text item. 3. Run semantic search right
   after the batch completes.
- **Expect:** the new clip appears with no perceptible delay; the `AppendEmbeddingsAsync`
  trace shows ~one batch appended, not a full-corpus reload.
- [ ]

---

## Phase 7 — Capture & update robustness (R7, R8)

Automated: `ClipboardMarkupDecoderTests` (CF_HTML overflow offsets), `ClipAngelImportServiceTests`
(size caps, hostile-file rejection, no batch on failure), `UpdateServiceTests` (https +
host-allowlist feed validation). Not automatable below.

### MT-7.1 — Update package signature verification
*Why manual:* needs a real signed Velopack release + the release pipeline.
1. Confirm `VelopackApp.Build()`/`UpdateManager` is configured with an embedded code-signing
   key. 2. Try to install a package signed with the wrong key. 3. Try a legitimately signed release.
- **Expect:** wrong/absent-signature packages are rejected before `ApplyUpdatesAndRestart`;
  correctly signed releases install. **This is the load-bearing check for R8 — verify before release.**
- [ ]

### MT-7.2 — Full update download -> apply -> restart
*Why manual:* requires a Velopack-installed instance + real HTTPS release feed.
1. Install via the Velopack installer. 2. Publish a newer version to the GitHub releases feed.
   3. Trigger update check, then "Restart & Install".
- **Expect:** the app restarts on the new version. (Feed http/allowlist rejection is unit-tested.)
- [ ]

### MT-7.3 — Live malformed/oversized RTF does not freeze the UI
*Why manual:* needs a real pathological RTF payload on the OS clipboard.
1. Copy a very large/malformed RTF (e.g. from a big Word doc). 2. Select that clip in the preview.
- **Expect:** preview renders within ~3s or falls back to plain text on timeout; the UI never
  freezes during conversion.
- [ ]

### MT-7.4 — SQLite failure during capture is surfaced, not swallowed
*Why manual:* deterministically forcing a `SqliteException` mid-capture needs an external lock.
1. Lock the DB from a SQLite browser. 2. Copy something while Clipthrough is capturing.
- **Expect:** a user-visible error notification appears and a `TraceError` is logged ("capture
  failed unexpectedly"); no crash, no silent data loss.
- [ ]

---

## How to add to this file

When you implement something whose behavior can only be confirmed in a real environment
(hardware, another machine/user, real network, the OS clipboard/DPAPI/update services, or
human-perceived UI timing), add an `MT-x.y` entry here with: why it can't be automated, exact
steps, and the expected result. Prefer an automated test whenever the behavior is reachable in
`dotnet test` — this file is the fallback, not the default.
