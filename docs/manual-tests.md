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

---

## How to add to this file

When you implement something whose behavior can only be confirmed in a real environment
(hardware, another machine/user, real network, the OS clipboard/DPAPI/update services, or
human-perceived UI timing), add an `MT-x.y` entry here with: why it can't be automated, exact
steps, and the expected result. Prefer an automated test whenever the behavior is reachable in
`dotnet test` — this file is the fallback, not the default.
