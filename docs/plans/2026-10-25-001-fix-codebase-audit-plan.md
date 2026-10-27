# fix: Codebase audit 2026-10-25 — storage-lifecycle safety, realistic-size performance, promise gaps, test gate

audited_commit: d006c658083e08b485bad9b271b15129cfac74f8
origin: artifacts/codebase-audit/2026-10-25/report.md (gitignored; local to the auditing checkout)
mode: full (first run of the `codebase-audit` skill; the previous whole-repo round was 2026-06-10-001)
status: **open decisions resolved by the owner (2026-10-26); phase approval still pending.** No unit may start until its phase is approved. **Updated 2026-10-26 with live E2E findings F-46 to F-51, including a new Phase 0.**

## Summary

**E2E update (2026-10-26).** Live runs of the real app added seven findings: **F-46 (P0)**, F-47 (P1), F-48 and F-49 (P2), and F-50, F-51 and F-52 (P3). That brings the total to **52 findings: 1 P0, 11 P1, 26 P2 and 14 P3**, with 43 Confirmed. The new findings change the plan as follows:
- **New Phase 0 (first-run hotfix):** F-46, F-49 and F-51. It goes first and blocks nothing else.
- **New units in Phase B:**
  - U-B7 (F-47): the preview import.
  - U-B8 (F-48): the Markdown→HTML guard.
  - U-B9 (F-50): startup hydration.
  - U-B10 (F-52, P3, found in the foreground run): self-paste suppression that survives Remote Desktop clipboard re-publishing.
- **U-G5 (F-42)** gains the false "saved as plaintext" labels.
- **Phase D priority for encrypted libraries.** At 60k, every scan these units remove costs about 10× more under SQLCipher (15–18 s instead of 1.4–1.9 s; see the report's encrypted-performance table). That strengthens U-D's index units for encrypted users, without reordering phases.

The original summary follows.

45 findings survived the critic pass: 0 P0, 10 P1, 24 P2 and 11 P3. Of those, 36 are Confirmed, 8 Probable and 1 a Suspicion. The previous hardening round holds, and all 335 curated mutants are killed.

What remains falls into five areas:
- **Storage-lifecycle edge cases** that lose or lock the library: F-01, F-02, F-15, F-16, F-22.
- **Performance at the default 1 GB size cap**: F-05 to F-10 and F-24 to F-27. Mostly indexes and constants, plus one schema change.
- **Product promises not kept on every path**: F-03, F-18, F-19, F-20.
- **Duplicated clip-to-clipboard pipelines**: F-04, F-12.
- **A CI gate that skips the headless suite**: F-29.

The owner closed one finding as an accepted risk (F-37, OD-11). The plan fixes the other 44, with one partial: F-21 gets hardening now plus an evaluation, and adopting any replacement package needs its own approval. Finding IDs are the report's.

## Settled decisions respected

All of `artifacts/codebase-audit/2026-10-25/settled-decisions.md` (S1–S35 and closed findings #1–#30). Three findings re-raise a settled decision, each naming the assumption that changed:
- **F-06 re-raises S16 / #3/#8.** The assumption was that leaving the BLOB out of the SELECT avoids reading it. That only holds for columns stored before the BLOB: measured 1,375 ms against 10.5 ms.
- **F-29 re-raises S33.** The exclusion was accepted for fast local runs, without weighing its CI cost: 92 of 335 guards are invisible to CI, and two data-loss mutants survive. F-30 also shows that not every headless failure is cleanup noise.
- **F-38 re-raises closed #7 / R8.** The assumption that package signatures are verified does not hold.

Not touched: MWVM god-object split (S2), static context-menu list (S5), DllImport style (S9), CA1001/CA2213 (S29), VellumText JSON exclusion (S31).

### New settled decisions from this audit (owner, 2026-10-26)

Add these to the repo's settled decisions (AGENTS.md or a docs/solutions note) when the corresponding phase lands. A future audit must not re-raise them without naming a changed assumption.

- **S36 — accepted risk (OD-11, F-37).** `OPENAI_API_KEY` is used as the bearer token for whatever AI base URL is configured, including a non-loopback `http://` endpoint. This is current behaviour and stays.
  - Assumption: a user who configures a cleartext endpoint while that environment variable is set accepts that the key travels to it.
  - Revisit if the env-key fallback is ever applied without the user configuring the base URL themselves.
- **S37 — update signing deferred (OD-5).** The feed is pinned to the repository path (U-C7). Whether to Authenticode-sign and verify releases is a separate decision. Until then, release integrity rests on the GitHub account and CI token. F-38 is closed only as far as the feed pin goes.
- **S38 — regex search engine (OD-7).** Regex search uses .NET `RegexOptions.NonBacktracking` by default. A user setting switches to the backtracking engine, which has a 250 ms match timeout; a timeout is reported as "pattern too slow".
  - Assumption: the default engine never needs backreferences or lookarounds. Users who need them opt into backtracking.
- **S39 — clipboard-history opt-out (OD-10).** Content published with `CanIncludeInClipboardHistory` = 0 is not captured while the new setting is on. The setting defaults to on.
- **S40 — search history (OD-9).** A query is recorded only on Enter or on selecting a result, never on a throttled refresh.
- **S41 — quitting inside the undo window (OD-8).** Pending deletes are committed on exit.
- **S42 — rekey password whitespace (OD-12).** The Rekey dialog rejects a new password with leading or trailing whitespace. Trimming stays consistent with the unlock prompt and `StorageOptions.Normalize`.

## Resolved decisions

| OD | Decision | Effect on this plan |
|---|---|---|
| OD-1 | (a) Separate payload table plus deduplicated `app_icons`, migrated once, resumably | Phase E is in scope as U-E1; it depends on Phase D |
| OD-2 | (b) Start evaluating a replacement encrypted-SQLite package now, as its own unit with on-disk compatibility verification; ship U-C5 meanwhile. **Then, on the evaluation (owner, 2026-10-27): (a) adopt `SQLite3MC.PCLRaw.bundle` 2.6.1 in SQLCipher v4 legacy mode**, landing before or together with Phase E's migration | New unit U-C8 (evaluation only); adoption is U-C9, with its own approval recorded here and its risks under U-C9 |
| OD-3 | (a) Editor and Compare write to a per-session temp folder that is deleted afterwards, with no `metadata.json`. Explicit Export stays in Documents but asks for confirmation on sensitive clips | U-C3 as written |
| OD-4 | (a) Run expiry before the startup backup; purge sensitive rows from backup copies **only while the sensitive-lifetime setting is on** (refined by the owner during Phase C: with it off, backups keep sensitive clips); add `secure_delete` after measuring its cost on the Phase D fixture | U-C4 split into U-C4a (expiry order and purge) and U-C4b (`secure_delete`, depends on U-D0) |
| OD-5 | (a) Pin the feed to the repo now; decide on signing separately | U-C7 is the pin only. Signing is out of scope (S37) |
| OD-6 | (a) Separate headless CI step that re-runs only cleanup failures; F-30 comes first | U-F1 is a hard prerequisite of U-F2 |
| OD-7 | NonBacktracking is the default, with a setting to switch to backtracking. Backtracking keeps the 250 ms timeout and the "pattern too slow" status (owner's own option) | U-B6 reworked: engine setting, tests for both engines; S38 |
| OD-8 | (a) Commit pending deletes on exit | U-B4 as written; S41 |
| OD-9 | (b) Record search history only on Enter or result selection | U-G8 reworked; S40 |
| OD-10 | (b) Honour `CanIncludeInClipboardHistory = 0` behind a setting that defaults on | U-C1 gains a setting; S39 |
| OD-11 | Keep current behaviour | U-C6 **dropped**; F-37 is an accepted risk (S36) |
| OD-12 | (a) Reject leading/trailing whitespace in the Rekey dialog and trim consistently | U-A2 as written; S42 |

## Phases

| Phase | Findings closed | Theme | Depends on | Parallel? |
|---|---|---|---|---|
| **0** | **F-46, F-49, F-51** | **First-run hotfix: new users and encrypted-library users can reach the app** | — | **ships first; can go out as a patch release on its own** |
| A | F-01, F-02, F-15, F-16, F-22 | Storage lifecycle cannot lose or lock the library | — | yes |
| B | F-04, F-11, F-12, F-13, F-14, F-17, **F-47, F-48, F-50, F-52** | Capture, transform, delete, search and preview correctness | U-B8 coordinates with U-B1 (same transform path) | yes |
| C | F-03, F-18, F-19, F-20, F-21 (hardening + evaluation), F-38 (feed pin) | Keep the product's promises on every path | U-C4b needs U-D0; U-C8 informs U-A3 and Phase E | yes, except U-C4b |
| D | F-05, F-07, F-08, F-09, F-10, F-24, F-25, F-26, F-27 | Performance at realistic size: indexes, plans, bounds | U-D0 first within the phase | yes; coordinate `DatabaseInitializer` edits with A and E |
| E | F-06, F-39 | Payload storage layout (OD-1a) | D; U-C9 lands before or with it | no |
| F | F-30, F-29, F-31, F-32, F-33, F-34, F-44, F-45 | Test gate and unguarded high-risk code | U-F1 before U-F2 | yes |
| G | F-23, F-28, F-35, F-36, F-40, F-41, F-42, F-43 | Consolidation, dead code, docs, history recording | A (U-G1 touches the `StorageOptionsService` connections) | after A |

Changes from the pre-decision plan:
- **Phase A** no longer waits on a decision.
- **Phase C** lost U-C6 and gained U-C8. U-C4 is split so that `secure_delete` waits for the Phase D fixture.
- **Phase E** is confirmed (OD-1a). It now also waits on U-C8, because a package switch would change the engine that runs the migration.
- **Phases B and C** each gain a user setting: the regex engine and the clipboard-history opt-out.
- **Phase G's search-history unit** now changes when history is recorded, instead of de-duplicating prefixes.

### How every unit is done

Every unit follows Phase 6 of the skill:
1. Write the regression test.
2. Watch it fail on the unfixed code.
3. Fix.
4. Add the mutant to `Clipthrough.Tests/Mutation/mutants.json` and run it. It must be KILLED and must compile cleanly, per AGENTS.md.
5. Run `-ValidateOnly`.
6. Run the full suite.
7. Commit.

Units that add a setting also follow the AGENTS.md "add a feature" template:
- the `AppSettings` field, with its `Normalize` default;
- the `SettingsViewModel` draft and the `LoadSettingsDraft`/`SaveSettingsAsync` wiring;
- `SettingsWindow.axaml` (compiled bindings);
- `AppText`;
- the README.

## Units

### Phase 0 — first-run hotfix (E2E, 2026-10-26)

**U-01 (F-46, P0) — the main workspace shows after "Create library" and after unlock**
- Change: in `MainWindowViewModel.cs`, replace the two guards at `:1878` (`IsWelcomeOpen`) and `:1907` (`IsPasswordPromptOpen`) with an equality check before setting:
  ```csharp
  if (_isWelcomeOpen == value) return;
  this.RaiseAndSetIfChanged(ref _isWelcomeOpen, value);
  this.RaisePropertyChanged(nameof(IsMainWorkspaceVisible));
  ```
  ReactiveUI 23's `RaiseAndSetIfChanged` returns the new value, not a changed flag.
- Tests: headless tests against the real `MainWindow`, promoted from the audit probe `e2e/wt/Clipthrough.Tests/AuditProbes/WelcomeFirstRunProbe.cs`.
  - **Welcome path:** start in first-run state, complete Create library, then assert that the workspace grid `IsEffectivelyVisible`, that the top menu is visible, and that `IsMainWorkspaceVisible` was raised.
  - **Unlock path:** open the password prompt, submit the correct password, then make the same assertions.
  - Both tests must fail on the unfixed code. The probe showed `vm=True`, `grid=False`, with 9 controls visible.
- Mutants: restore the `!this.RaiseAndSetIfChanged(...)` guard in each setter, as two mutants.
- No other site in `Clipthrough/` uses the return value of `RaiseAndSetIfChanged` (checked by grep), so this is not a recurring pattern and needs no analyzer guard.

**U-02 (F-49) — the welcome footer is reachable on narrow work areas**
- Change: make the welcome content, including the footer, scrollable in both directions, or move the footer inside the scroll viewer. Let the window fit a work area narrower than 1100 DIP while the welcome screen is open.
- Test: in a headless window at 576×1200 DIP, the "Create library" button's bounds lie inside the window's client area. It also stays Tab-reachable.
- Mutant: restore `MinWidth="1100"` with the footer outside the scroller.

**U-03 (F-51) — no settings-mirror warning on first run**
- Change: skip the legacy settings mirror in `SettingsService` while no database exists yet, instead of tracing a warning (`:453`).
- Test: a first-run save against an empty data folder logs no warning, and the mirror is written once the database exists.
- Mutant: remove the existence check.

### Phase A — storage lifecycle

**U-A1 (F-01) — moving onto a folder with leftover sidecars**
- Change: snapshot an existing target with `SqliteDatabaseCopier` into the `.before-move-*` safeguard, and stash the target's `-wal` and `-shm` before `File.Move`. This mirrors `DatabaseBackupService.RestoreCore`.
- Test: promote `AuditProbe_PathMoveOntoTargetWithLeftoverWal_ReplaysForeignFrames` (audit-correctness) into `StorageOptionsServiceTests`. The moved database must return the source rows.
- Mutant: delete the line that stashes the target's sidecars.

**U-A2 (F-02, OD-12a) — rekey password whitespace**
- Change: the Rekey dialog rejects a new password with leading or trailing whitespace, with an `AppText` message. `RekeyAsync` trims both passwords using the same rule as `StorageOptions.Normalize`, and verifies with the value the session will use (`updated.DatabasePassword`).
- Tests:
  - the dialog/VM refuses `"correct horse "`;
  - at service level, after a rekey both `CanOpenWithPassword(path, Current.DatabasePassword)` and a fresh `SetInMemoryPassword(raw)` open the database.
- Mutant: verify with the raw argument again.

**U-A3 (F-16) — adding or removing encryption**
- Change: when either side is empty, use `ATTACH … KEY` + `sqlcipher_export()` inside the existing verify-and-swap.
- Tests: plain to encrypted opens only with the new key; encrypted to plain opens without one.
- Mutant: route the empty-side case back to `PRAGMA rekey`.
- U-C8 must confirm that any candidate package has an equivalent of `sqlcipher_export`.

**U-A4 (F-15) — backups after a rekey**
- Change: after a successful rekey, re-key the existing backups, or retake today's backup and mark the older ones, so that every listed backup restores.
- Test: backup, then rekey, then restore succeeds.
- Mutant: skip the loop that re-keys backups.

**U-A5 (F-22) — atomic config writes**
- Change: extract the atomic temp-file-plus-rename writer and quarantine from `SettingsService` into one helper. Use it for `storage.json`, the AI-key sidecar and `copilot-token.bin`. Surface a load fault on a torn `storage.json`.
- Test: a truncated `storage.json` leaves a quarantine file and reports a fault; the path is not silently reset to the default.
- Mutant: revert `SaveToDiskAsync` to `File.WriteAllTextAsync`.

Guard R3: a lifecycle matrix test class.

### Phase B — capture, transform, delete, search

**U-B1 (F-04) — row-path Markdown → HTML**
- Change: `ApplyTransformationToSingleClipAsync` uses `ProducesHtml`, then delegates to `ApplyTransformToTargetsAsync`.
- Test: the row-path Markdown → HTML transform sets `LastCopiedRichContentFormat == Html` and stores `Html`.
- Mutant: restore the `BoxTableToHtml` comparison.

**U-B2 (F-12) — hotkeys on image clips**
- Change: `ExecuteCustomHotkey` applies the `CanTransform` eligibility check. `PasteAtOffset` writes through `ClipClipboardWriter.TryCopyAsync` and marks the clip pasted only when that succeeds. Extract the handler bodies into testable methods.
- Tests:
  - an image clip with `builtin:UpperCase` leaves the clipboard unchanged and pastes nothing;
  - an image at the paste offset gives `BitmapCopyCount == 1`.
- Mutants: drop the eligibility check; restore `CopyTextAsync` in `PasteAtOffset`.
- Live evidence (2026-10-26): the text pasted for an image is its stored `content`, which is the source window title. The tests should use a realistic title such as `Q3 layoffs - Outlook`, and assert it never reaches the clipboard.

**U-B3 (F-13) — re-capture during the undo window**
- Change: withdraw a re-captured id from `_pendingDeletes` and `_hiddenPendingDeletes`.
- Test: the probe `AuditProbe_RecopyDuringUndoWindow_NewCopyIsDeletedByCommit`.
- Mutant: remove the withdrawal.

**U-B4 (F-14, OD-8a) — pending deletes on exit**
- Change: commit pending deletes in `Dispose`.
- Test: delete, then `Dispose`; the row is gone from the store.
- Mutant: call `CancelAllPendingDeletes` again.

**U-B5 (F-17) — embeddings for clips that become sensitive**
- Change: make the embedding save conditional on `is_sensitive = 0 AND embedding_status = 'processing'`. When an OCR result marks a clip sensitive, purge its vector and set `excluded`.
- Test: claim, then mark sensitive, then save. No vector remains and the status is `excluded`.
- Mutant: drop the `is_sensitive` predicate.

**U-B6 (F-11, OD-7) — regex search engine setting; no search can hang**

*Setting*
- Add an `AppSettings` field selecting the regex search engine. Suggested shape: an enum `RegexSearchEngine { NonBacktracking, Backtracking }`, default `NonBacktracking`.
- `AppSettings.Normalize` maps an unknown value to `NonBacktracking`.
- Show it in Settings under search, with an `AppText` label and a one-line explanation: backtracking supports backreferences and lookarounds but can be slow.
- Carry it to the store on `ClipSearchFilters` (e.g. `RegexEngine`), so that `ClipStoreService.BuildSearchRegex` stays a pure function of the filters.

*NonBacktracking (default)*
- Construct with `RegexOptions.NonBacktracking`. Keep `IgnoreCase` and `CultureInvariant`; drop `Compiled`, which NonBacktracking does not use.
- A pattern the engine rejects (backreferences, lookarounds, atomic groups) throws `NotSupportedException` when it is constructed. Report that as its own status, "pattern needs the backtracking engine", naming the setting — not as a generic invalid pattern.

*Backtracking*
- Keep today's options and add a 250 ms match timeout.
- A `RegexMatchTimeoutException` during the scan stops the search and shows a "pattern too slow" status. The list keeps its previous results.

*Both engines*
- Pass the refresh `CancellationToken` into `SearchAsync` (shared with U-D4) so a superseded search stops, and a later blank search refreshes even after a slow one.

*Tests*
- `(a+)+$` over a 41-character near-miss clip completes under NonBacktracking within an asserted bound (e.g. under 2 s) and returns no match.
- `(a+)+$` under Backtracking returns within about 1 s with the "pattern too slow" status, and a following blank search still refreshes the list (the queue is not stuck).
- `(\w)\1` under NonBacktracking gives the "needs backtracking" status. Under Backtracking it matches `"aa"` and not `"ab"`.
- Setting round-trip: the default is NonBacktracking; saving and loading preserves Backtracking; `Normalize` maps an out-of-range value to the default; the Settings draft reverts on Cancel.
- Equivalence: an ordinary pattern (`inv\w+ 20\d\d`) returns identical results under both engines, on a hand-written fixture rather than with the old code as the oracle.

*Mutants (each must be KILLED)*
- the default flipped to `Backtracking`;
- the timeout removed from the backtracking constructor;
- `RegexEngine` ignored in `BuildSearchRegex`, so only one engine is ever used;
- the timeout exception swallowed as "no match".

**U-B7 (F-47, P1) — the preview import cannot multiply or outlive itself**
- Change, in three parts, all inside `RichDocumentView`:
  1. **Single flight.** A selection imports its content once, however many rendering surfaces show it. Remove the duplicate import seen live (two "import timed out" messages per selection).
  2. **Stop on timeout.** A new selection or a timeout stops the in-flight import from being retained. Today `Task.Run(...).WaitAsync(ImportTimeout)` (`:310`) abandons the task but lets it keep allocating. With the current synchronous importer, run the import on a dedicated worker whose result is dropped and whose next import waits for it to finish, so that at most one import is ever in flight.
  3. **Structural pre-screen.** Before importing, scan the markup for table-span products (`colspan × rowspan` summed per table) over a fixed cap, and fall back to the plain-text preview. This is the same mechanism as the existing size cap and unbroken-run guard, which bound length only.
- Tests:
  - A view given the 32 × `colspan=1000 rowspan=1000` fixture falls back without calling the importer. Use an importer seam that counts calls.
  - Two surfaces bound to one selection call the importer once.
  - A selection change during a slow fake import leaves at most one import in flight.
- Mutants: remove the span pre-screen; import per surface.
- Upstream (hand-off to the VellumText audit, not this plan): clamp the colspan/rowspan expansion in the importer. When the async-only importer is adopted (see *Dependencies outside this plan*), use its cancellation, if it offers one, for part 2.

**U-B8 (F-48) — Markdown→HTML cannot crash the process**
- Change: guard `TextTransformationService.Apply(MarkdownToHtml)` (`:331`) and `HtmlToMarkdown` (`:298`) with the preview's unbroken-run rule plus a nesting-depth pre-scan for `[`, `>`, list markers and HTML open tags. Over the limit, the transform returns the input unchanged, and reports a status message (new `AppText` entry). Coordinate with U-B1, which reroutes the same single-clip path.
- Test: run the production function in a child process (the harness pattern from `e2e/wt/AuditE2E/VellumHarness`) on 8,192 × `[`. Assert exit code 0 and unchanged output. Unguarded, the same test exits with 0xC00000FD.
- Mutant: disable the depth pre-scan.

**U-B9 (F-50) — the startup selection is hydrated**
- Change: after the first refresh selects a clip, call `EnsureContentHydratedAsync` for it, as user-driven selection does. This respects S16: list rows stay payload-free.
- Test: a headless restart with a rich newest clip. The preview receives HTML markup, not the flattened plain text.
- Mutant: skip hydration on the startup path.

**U-B10 (F-52) — a self-paste stays suppressed when a redirector re-publishes it**
- Change: `ClipboardSuppressionGate` remembers a hash of the content Clipthrough wrote (passed to `Arm`) and suppresses every change event whose content matches it until the window expires, instead of exactly one event. Content that doesn't match is still captured, even inside the window.
- Tests (gate level, with the injectable clock):
  - one `Arm(hash)` followed by two events with the same hash suppresses both;
  - a different hash inside the window is captured;
  - the same hash after the window expires is captured.

  Plus a monitor-level test that feeds an extra `Terminal Services Remote Clipboard Origin` event after a paste.
- Mutant: revert to the single-shot `Interlocked.Exchange(ref _pending, 0)`.
- Update the AGENTS.md auto-copy contract to say suppression is by content, not by event count.

### Phase C — promises on every path

**U-C1 (F-03, OD-10b) — honour the clipboard-history opt-out**
- Setting: add an `AppSettings` field, e.g. `HonorClipboardHistoryOptOut`. It defaults to **true**, which is also the `Normalize` default for older files. Expose it in Settings near the existing capture-exclusion caveat, and update the `AppText.cs:65` text to mention the format. Update the README.
- Behaviour: when the setting is on, `ShouldIgnoreClipboard` treats `CanIncludeInClipboardHistory` with a DWORD value of 0 as an opt-out. When it is off, only today's two formats apply. A nonzero value, or a missing format, never suppresses capture.
- Unit tests, one per case:
  - DWORD 0 with the setting on: ignored;
  - DWORD 0 with the setting off: captured;
  - DWORD 1: captured;
  - malformed (too short) data: captured and traced.
- Also a settings round-trip test for the default.
- Mutants: remove the new format from the check; ignore the setting.

**U-C2 (F-20) — "Open source URL"**
- Change: `OpenSelectedClipSourceUrlAsync` calls `OpenUrlAsync`.
- Test: a VM test with `TestSystemInteractionService` recording which method was called; `\\host\x` is rejected.
- Mutant: revert to `OpenPathAsync`.

**U-C3 (F-18, OD-3a) — plaintext export copies**
- Change: Open-in-editor and Compare write to a per-session temp root, deleted after the tool exits or when the app exits, with no `metadata.json`. Explicit Export keeps `Documents\Clipthrough Exports`, but asks for confirmation (`AppText`) when any exported clip `IsSensitive`.
- Tests: an editor export lands under the session temp root and is gone after disposal; exporting a sensitive clip prompts, and cancelling writes nothing.
- Mutant: point the root back at `MyDocuments`.

**U-C4a (F-19, OD-4a) — expired sensitive clips in backups**
- Change: run the sensitive-lifetime expiry before the startup daily backup. While the sensitive-lifetime setting is on, purge `is_sensitive = 1` rows, with their FTS rows and vectors, from each backup copy before it is finalised. With the setting off, backups keep sensitive clips (owner decision, Phase C).
- Test: invert `Probe_ExpiredSensitiveClipSurvivesInBackupAndFreePages` for the backup half.
- Test (owner decision): with the lifetime off, the backup retains sensitive clips; with it on, they are purged. Mutant: drop the condition.
- Mutant: skip the backup purge.

**U-C4b (F-19, OD-4a) — `secure_delete` (depends on U-D0)**
- Measure first: on the U-D0 fixture (60k clips, 1 GB), record capture, retention-purge and delete costs with and without `PRAGMA secure_delete = ON`, both encrypted and unencrypted.
- Then enable it on every connection, in `SqliteConnectionFactory`'s `StateChange` handler next to `busy_timeout`. Record the numbers in the commit and a docs/solutions note.
- If the cost is user-visible (e.g. capture slower by more than about 10%), stop and return to the owner with the numbers instead of shipping.
- Test: the free-page half of the probe — the live file no longer contains the expired secret's bytes.
- Mutant: remove the pragma.

**U-C5 (F-21, OD-2) — ClipAngel import hardening (ships whatever U-C8 concludes)**
- Change: import only when `Clips` is a real table and the schema has no views or triggers. Open with `trusted_schema=OFF` and `query_only`.
- Test: a fixture whose `Clips` is a view is rejected with a clear status.
- Mutant: drop the `type='table'` check.

~~**U-C6 (F-37)**~~ — **dropped**. OD-11 keeps current behaviour; accepted risk S36.

**U-C7 (F-38, OD-5a) — update feed pin**
- Change: pin the feed allow-list to the path prefix `https://github.com/lurebat/Clipthrough/`. Rewrite the "Prevents feed hijacking" comment to say what the pin covers and what it does not (no signature check, S37).
- Test: `https://github.com/other/repo/...` is rejected; the real feed is accepted.
- Mutant: a host-only check.

**U-C8 (F-21, OD-2b) — evaluate a replacement encrypted-SQLite package (new; evaluation only, no adoption)**

*Candidates*
- SQLite3MultipleCiphers through its SQLitePCLRaw bundle, in SQLCipher-compatible mode;
- a current SQLCipher build (community source build or commercial package; check the licence);
- any maintained SQLitePCLRaw bundle that ships SQLite 3.50.2 or later with SQLCipher-format support.

*Verify, in a scratch worktree, against databases created by the current build*
1. Existing databases open: plaintext, encrypted with SQLCipher 4.5.2 defaults, rekeyed, and with a `-wal` present. All rows read back and `integrity_check` returns `ok`.
2. Round trip: write with the candidate, then reopen with the current build. If that fails, document it. It would make adoption a one-way migration, which blocks rollback.
3. Features we use: FTS5 with the trigram tokenizer and the existing triggers; the Online Backup API (`SqliteDatabaseCopier`); `PRAGMA rekey`; the `sqlcipher_export` equivalent that U-A3 needs; `busy_timeout` and WAL behaviour.
4. KDF and cipher parameters match the current ones (PBKDF2-HMAC-SHA512, 256k iterations, HMAC-SHA512), or there is an explicit migration path.
5. Package size, native architectures shipped, and performance on the U-D0 fixture (search, capture, claim).
6. The embedded SQLite version and a CVE check, to close CVE-2025-6965.

*Deliverables*
- An evaluation note (in `docs/solutions/` or `docs/plans/`) with the compatibility matrix and a recommendation.
- A reusable compatibility test fixture: databases produced by the current build, committed or generated. A future adoption unit must pass it.

*Adoption*
- Adoption is a **separate later unit that needs owner approval** (a new dependency, Iron law 3).
- If adopted, it must land before or together with Phase E's migration. Otherwise Phase E must be re-verified on the new engine.

**U-C9 (F-21, OD-2) — adopt SQLite3 Multiple Ciphers (owner decision, 2026-10-27)**

*Decision*: replace `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 (SQLite 3.39.2, deprecated, no further releases) with `SQLite3MC.PCLRaw.bundle` 2.6.1 (SQLite 3.54.0) in SQLCipher v4 legacy mode (`cipher=sqlcipher&legacy=4`). The on-disk format does not change, so every installed library opens as it is and a rollback to the old build can still read what the new one writes (evaluation, §2). It lands before or together with Phase E.

*Required by the owner*
- One connection-string / cipher-recipe helper that every connection goes through, with a guard test proving nothing bypasses it.
- The fixture compatibility test over all four existing formats (plaintext WAL, SQLCipher 4.5.2, rekeyed, encrypted with an uncheckpointed `-wal`).
- Tests and mutants for the replacement rekey path, which replaces `sqlcipher_export`.
- The 60k performance comparison against the current bundle, on a quiet machine requested through the coordinator.

*Risks accepted with the decision*
1. **Untested dependency pairing.** Microsoft.Data.Sqlite 10.0.11 declares `SQLitePCLRaw.core` 2.1.x; this bundle needs 3.x, so the app runs MDS 10 on core 3.0.2. Microsoft has not tested that pair. It is pinned explicitly, and our suite plus the evaluation harness are the only evidence it works. Re-check on every bump of either package (S32).
2. **Single maintainer.** The package is published by one person (U. Telle), MIT-licensed, with a package id that is not prefix-reserved; the native binaries are Authenticode-signed (SignPath Foundation). Releases are frequent today; nothing guarantees they continue.
3. **Format fork on a missed recipe.** A connection without the recipe silently uses the library's default cipher: an existing library fails with error 26 ("wrong password") and a new one is unreadable by any SQLCipher build. Mitigated by the single factory and its guard, which also covers the test tree.
4. **Engine jump 3.39.2 -> 3.54.0** beyond the evaluation's fixtures (query plans on 60k clips) is measured only by the quiet-machine comparison.
5. **No non-Windows run.** Linux and macOS natives ship in the package but were not exercised.

*Measured (2026-10-27, quiet machine, U-D0 fixture: 60k clips, ~1.6 GB, Release; the current bundle and SQLite3MC on the same Phase D code, each run from a fresh copy of one seeded library)*
- **Controlled comparison** (each probe group run alone on a fresh copy, so both engines saw the same state):
  - Search (P1), encrypted: SQLite3MC is faster on every query except FTS fuzzy (5.2 -> 6.2 ms) and FTS no-match (0.3 -> 0.5 ms). Examples: the image filter 2.6 s -> 1.1 s; text-filter sorts about 6 s -> 3 s; regex no-match 15.3 s -> 6.6 s; case-sensitive 2.5 s -> 1.2 s.
  - Search (P1), plaintext: equal within noise, for example blank Alphabetical 243 vs 231 ms and case-sensitive 365 vs 359 ms.
  - Capture (P4), encrypted, large clips: about 2x faster (re-copy of 1.9 MB 261 -> 110 ms).
- **Full probe runs are confounded and are not used as evidence of a regression.** xUnit ran the probes in a different order for the two builds: the old bundle ran P1 last, SQLite3MC ran it fourth, right after P12 rewrote 58,903 embedding statuses. In those runs the old bundle looked faster on alphabetical, case-sensitive and whole-word search (plaintext 108-119 vs 254-362 ms), and on two single-sample probes: MarkAllEmbeddingsForRerunAsync (21.8 vs 37.9 s, plaintext only) and the first blank search after the stats cache is invalidated (5 vs 42 ms plaintext, 33 vs 165 ms encrypted). The isolated reruns removed the first group. The other two were not rerun in isolation and stay open: measure them with a fixed probe order before reading anything into them.
- Encrypted full runs took 10.8 min with SQLite3MC against 18.4 min with the current bundle.

### Phase D — realistic-size performance

**U-D0 — benchmark fixture (prerequisite for every D unit, U-C4b and U-C8)**
- Promote the audit's `PerfLibrary.cs` (audit-perf) into an opt-in benchmark fixture: 60k clips and 1 GB, with an encrypted variant.
- Exclude it from the default and CI test runs.
- Each D unit reports before and after numbers from it (guard R2).

**U-D1 (F-05) — embedding claim**
- Change: pin the claim's query plan (`INDEXED BY`, or a `UNION ALL` of the backlog states). Move the SELECT out of the IMMEDIATE transaction.
- Test: `EmbeddingClaimIndexTests` asserts `idx_clips_embedding_backlog` by name.
- Mutant: remove `INDEXED BY`. The test must then fail; today's test would pass.

**U-D2 (F-09) — content-type filter index**
- Change: add a MostRecent index prefixed by content type, following the complete-set rule.
- Test: the plan test asserts this index for `content_type IN`.
- Mutant: drop the index from `SortOrderIndexes`.

**U-D3 (F-08) — broad-search threshold**
- Change: re-measure on U-D0 and lower `BroadSearchMatchThreshold`.
- Test: at 500 matches, the ordered-index plan is chosen.
- Mutant: restore 2000.

**U-D4 (F-07) — FTS prefilter for case-sensitive, whole-word and wildcard search**
- Change: prefilter candidates with FTS when every token is at least 3 characters. Share the CancellationToken plumbing with U-B6.
- Regex mode is not prefiltered; the U-B6 engine choice bounds its cost instead.
- Test: equivalence with the unfiltered path on a hand-written fixture.
- Mutant: skip the prefilter's post-match.

**U-D5 (F-10) — bounded list text**
- Change: bound the formatter's input to a prefix. Then bound the list read and hydrate the full text when a clip is selected.
- Tests: the title and snippet of a 2 MB clip equal those of its first 4 KB; a list entry has `Content.Length <= N`, and hydration restores the full text.
- Mutant: remove the prefix bound.

**U-D6 (F-25) — startup scans and coverage refreshes**
- Change: partial indexes for `sensitivity_scanned_at IS NULL` and for OCR coverage, plus an in-flight guard on the coverage refresh.
- Tests: the plan uses the partial index; two overlapping triggers run one query.
- Mutant: drop the guard.

**U-D7 (F-26) — FTS re-tokenising unchanged text**
- Change: add a `WHEN` clause to `clips_au`.
- Test: index correctness is unchanged, and no FTS work happens when the content is unchanged.
- Mutant: remove the `WHEN`.

**U-D8 (F-24) — long write-lock holds**
- Change: UPDATE only the rows that change, and batch rerun-all.
- Test: on a 5k fixture, a capture issued during rerun-all succeeds.
- Mutant: a single unbatched UPDATE.

**U-D9 (F-27) — semantic cache removal**
- Change: remove entries with tombstones instead of copying the whole vector array.
- Tests: a removal does not allocate a new vectors array, and removed ids never come back.
- Mutant: restore the copy path.

### Phase E — payload layout (OD-1a)

**U-E1 (F-06, F-39)**

*Schema*
- New tables: `clip_payloads(clip_id INTEGER PRIMARY KEY REFERENCES clips(id) ON DELETE CASCADE, content_bytes BLOB)` and `app_icons(hash TEXT PRIMARY KEY, png BLOB)`.
- `clips` gains `source_app_icon_hash`.

*Migration: one-time and resumable*
- Copy in id-range batches, one transaction per batch, with progress recorded in `app_metadata`.
- Switch the readers over only after the copy completes; drop or null the old columns last.
- Never hold a single `DatabaseMaintenanceScope`-length transaction over 1+ GB.
- Move the `stored_bytes` triggers along with the data.

*Readers*
- `GetByIdAsync` and hydration join `clip_payloads`. Icons resolve through the hash.

*Tests*
- The migration is idempotent and resumes after an interruption injected mid-batch.
- Identical icons are stored once.
- Every lazy-hydration test stays green.
- On U-D0, scans that read columns after the blobs perform at the "before blob" figure.

*Mutant*: hydration skips the payload join.

*Dependencies*: Phase D, and U-C9 (the engine switch) lands before or with it. The migration is verified on the new engine.

### Phase F — test gate and unguarded code

- **U-F1 (F-30) — wait for the database in headless tests (hard prerequisite of U-F2).** Add an `InitializeAndWaitForDatabaseAsync` helper and use it at every headless `InitializeAsync` call site. Regression check: the 1.5 s startup-delay injection leaves the class green; record this as an inverse check in the mutant's `why`.
- **U-F2 (F-29, OD-6a) — headless tests in CI.** Add a headless CI step that re-runs only `[Test Case Cleanup Failure]` once; assertion failures fail the build. Check: applying `delete-with-nothing-checked-takes-every-clip` fails CI.
- **U-F3 (F-31) — Copilot token persistence.** Add a token-path seam to `CopilotAuthService`, plus tests. Mutants: `copilot-token-written-unprotected`, `copilot-signout-leaves-token`.
- **U-F4 (F-32) — Copilot branch of the AI transform.** Add tests for the Copilot branch. Mutant: `copilot-token-sent-to-custom-base-url`.
- **U-F5 (F-33) — export path containment.** Add an export-root seam, plus containment tests. Mutant: `export-sanitizer-disabled`.
- **U-F6 (F-34) — ClipAngel import pipeline.** Split decryption from import, and test the import on a plain fixture. Mutant: `clipangel-blob-cap-removed`.
- **U-F7 (F-44) — `SystemInteractionService` helpers.** Make the helpers internal and add tests. Mutants: `openurl-accepts-file-scheme`, `command-template-ignores-placeholders`.
- **U-F8 (F-45) — search-history cap.** Save 60 entries and assert the newest 50 remain. Mutant: `search-history-unbounded`.

Guard R6.

### Phase G — consolidation, dead code, docs, history recording

- **U-G1 (F-28) — one connection factory.** Add a factory overload and use it at all seven sites. Guard R7: a source-scan test, itself mutation-checked by re-adding a literal pragma.
- **U-G2 (F-23) — one worker-lifetime owner.** A single owner holds the desired state and a pause count. Test: while a pause is held, `SettingsChanged` with semantic search on does not start the worker; after release, it does. Mutant: `ApplySemanticSearchWorkerState` starts the worker regardless of the pause.
- **U-G3 (F-40) — dead code.** Delete the dead members; set IDE0051 and IDE0052 to error (guard R5).
- **U-G4 (F-41) — Release packaging.** Make the DiagnosticsSupport reference Debug-only; remove the Tmds pin or comment on why it stays. Check: the Release output lacks the DLL.
- **U-G5 (F-42) — docs and false security labels.** Correct AGENTS.md, CONCEPTS.md, both skills and the `DatabaseBackupService` summary. Record S36–S42 where AGENTS.md keeps decisions.
  - Also correct the five "saved as plaintext in storage.json" strings: `MainWindow.axaml:1392`, `SettingsWindow.axaml:289`, `RekeyDialog.axaml:44,49`, and `AppText.cs:132,134`. They contradict S22, so they should describe DPAPI on Windows and in-memory-only storage elsewhere. Move the three AXAML literals into `AppText`.
  - Guard R4: extend `StorageSchemaDocTests`, plus a doc-symbol test.
- **U-G6 (F-43) — dropped image files.** Re-encode them to PNG. Test: the stored bytes of a dropped JPEG start with the PNG signature. Mutant: store the raw bytes.
- **U-G7 (F-35) — imported timestamps.** Convert to UTC at the store boundary, plus an idempotent backfill. Test through `ClipAngelImportService`, not a hand-built offset. Mutant: drop `ToUniversalTime()`.

**U-G8 (F-36, OD-9b) — record search history only on commit**
- Change: remove `SaveSearchAsync` from the path that runs when a refresh is applied (`MainWindowViewModel.cs:2478`). Record the current query only when:
  - the user presses Enter in the search box; or
  - the user selects or activates a result of a non-empty search (paste, copy or open of a result, or moving into the list with the keyboard).

  Use the existing single choke point for "result chosen", and record there once per query.
- Tests:
  - typing `inv`, pausing, then typing `oice 2024` with throttled refreshes records **nothing**;
  - Enter records `invoice 2024` once;
  - choosing a result records the query once;
  - an empty query is never recorded;
  - pressing Enter again on the same query does not create a duplicate (the existing de-duplication).
- Mutant: restore the save in the refresh path. The "nothing recorded while typing" test must fail.
- Update the README or help text if it describes history behaviour.

## Guards

| Pattern | Guard (lands in) |
|---|---|
| R1 clipboard pipeline divergence | ast-grep `SuppressNext()` / `BoxTableToHtml` comparison rules run from a test (U-B1/U-B2) |
| R2 payload-free perf calibration | U-D0 realistic fixture + plan-by-index-name assertions (U-D1) |
| R3 lifecycle happy-path only | lifecycle matrix test class (Phase A) |
| R4 doc drift | `StorageSchemaDocTests` columns/status + doc-symbol test (U-G5) |
| R5 dead members | IDE0051/IDE0052 at error with `EnforceCodeStyleInBuild` (U-G3) |
| R6 untestable services | file-root ctor seams + "every Services type has a real-instance test" reflection test (Phase F) |
| R7 connection policy copies | `PRAGMA busy_timeout` source-scan test (U-G1) |

## Dependencies outside this plan

**VellumText async-only importer API** (owner decision in the VellumText repo, 2026-10-26). The importer API becomes async-only: the sync `Import` is removed, and the editor always runs importers off the UI thread.

This plan does **not** adopt that version. Clipthrough stays on VellumText 0.7.0 through Phases A–G. The adoption is a separate change, and these known call sites must move to the async API:

- **`Clipthrough/Controls/RichDocumentView.cs:339-345`** (`Import` → `HtmlFormat.Instance.Import` / `RtfFormat.Instance.Import`).
  - Today this already runs inside `Task.Run(...).WaitAsync(ImportTimeout)` (`:310`).
  - It becomes a direct `await` of the async importer, keeping the same timeout and fallback.
  - The generation check after the await (`:328`) must stay.
- **`Clipthrough/Services/Ai/TextTransformationService.cs:298`** (HtmlToMarkdown) **and `:331`** (MarkdownToHtml).
  - Both call the sync importer inside `TextTransformationService.Apply`, which AGENTS.md documents as a *pure static synchronous* function.
  - An async-only importer forces one of two changes. One is an async `Apply`, which touches every transform caller: the menu, the context menu, the custom-hotkey path in `App.ExecuteCustomHotkey`, and their tests. The other is a sync-over-async bridge, which AGENTS.md's threading rules rule out on the UI thread.
  - This changes the AGENTS.md "Text transformations" convention. Plan it together with Phase B's U-B1, which already reroutes the single-clip transform path.
- **Paste into the preview editor.**
  - The read-only `RichTextEditor` in `RichDocumentView` (`:144`, context menu `:229-250`) deliberately keeps paste inert, and Clipthrough has no custom paste importer today.
  - On adoption, verify that the editor's async paste path, now off the UI thread, still respects the read-only gate. This is the same class of check as the Ctrl+X / CutAsync note in AGENTS.md.
- **After the bump**, run the full mutation sweep (S32). Moving the importer off the UI thread can make guards there redundant, which is exactly what the AGENTS.md section "Re-run the mutants after a dependency bump" describes.
- **Related E2E findings.**
  - U-B7 (F-47) and U-B8 (F-48) ship on 0.7.0 with Clipthrough-side guards.
  - The async importer is their long-term home: cancellation for the preview, and off-UI-thread transforms.
  - The unbounded colspan/rowspan expansion (about 1.5 GB per import) and the nested `<template>`/Markdown recursion that overflows the stack are upstream VellumText defects. They are handed to the VellumText audit. Fixing them upstream does not let U-B7/U-B8 be skipped on 0.7.0. Evidence: `artifacts/codebase-audit/2026-10-25/e2e/logs/vellum-harness*.json`.

## Out of scope

- MainWindowViewModel decomposition beyond what a unit needs (S2).
- Adopting a replacement encrypted-SQLite package was out of scope until the owner approved it after U-C8 (OD-2, 2026-10-27); it is now U-C9.
- Signing and verifying updates (S37; decided separately).
- F-37 (accepted risk S36) and security F-10 (dropped by the critic); also the image-URL GET in `AiTransformService` (noted under F-37, not counted).
- Dependency upgrades other than the U-C8 evaluation, including the VellumText async-importer version above. Bumps to Avalonia, Velopack and Onnx are independent; re-run the mutants after any bump (S32).
- Non-Windows feature work (S8); `LibraryImport` conversion (S9); CA1001/CA2213 (S29).
- Pull requests: approving this plan, or a phase of it, is not approval to open PRs.
