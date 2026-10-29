---
title: FTS rebuild merge cost and the first start after an upgrade - research
date: 2026-10-28
status: research - decisions needed, nothing implemented
relates_to: docs/plans/2026-10-25-001-fix-codebase-audit-plan.md (U-E1, Out of scope / follow-ups, S37)
---

# FTS rebuild merge cost and the first start after an upgrade

Research only. Two problems on the road to 1.0, the options for each, and a
recommendation. Every option that changes what the user sees is marked
**[user decision]**.

Measurement conditions, so the numbers can be weighed:

- **60k figures:** taken on the U-D0 fixture (60k clips, about 1.6 GB, 700
  images) in coordinator-granted quiet windows on 2026-10-28. Each run used a
  fresh copy of the library, and builds alternated. During the last window
  another session was building at BelowNormal priority.
- **Small-fixture figures:** from `tools/fts-research/fts-debt.ps1`, run at
  BelowNormal on a shared, non-quiet machine. Treat them as indicating direction
  and order of magnitude only.

## 1. The search-index merge cost after a full rebuild

### What happens

`INSERT INTO clips_fts(clips_fts) VALUES('rebuild')` writes the index as a
series of level-0 segments, which it flushes as its in-memory hash fills, and
merges them incrementally as it goes (`automerge`, default 4). The rebuild ends
mid-merge. FTS5 then charges the outstanding merge work to later writes, in
proportion to how much each write adds. A large capture adds a lot, so it pays
a lot.

**At 60k** (U-E1 confirmation window), read from `clips_fts_data` id 10, the
structure record:

- The rebuilt seed held 5 levels and 13 segments. Level 3 held 4 segments of
  about 33k pages, and a merge was pending at level 2. The never-rebuilt seed
  had no level holding 4 segments.
- The first 1.9 MB capture on a fresh copy of the rebuilt seed took **2556 ms**
  plaintext and **4857 ms** encrypted. A second run on the same copy took
  662 ms, and a never-rebuilt library took 381-692 ms.
- Running `'optimize'` on that copy took **48 s**, after which its first capture
  took **407 ms**.

**Small fixture** (9,000 docs, 22 MB of text, 74 MB trigram index; same table
shape as `clips_fts`, one column):

| Variant | 1st capture | 2nd | 3rd | Notes |
|---|---:|---:|---:|---|
| A. After `rebuild` (27 s) | 1633 | 955 | 829 ms | Merges pending at L1 and L2 |
| B. `rebuild` + `optimize` | 874 | 723 | 868 ms | `optimize` 2.8 s, leaves 1 segment |
| C. `rebuild` + `('merge', 64)` until drained | 962 | - | - ms | 38 calls, 500 ms total, median 10 ms, max 59 ms |
| C'. `rebuild` + `('merge', 256)` until drained | 1403 | - | - ms | 11 calls, 511 ms total, max 115 ms |
| D. `automerge=0` after `rebuild` | 577 | 801 | 648 ms | No merge work on the write path; L0 grows |
| D'. `automerge=8` / `16` after `rebuild` | 709 / 620 | 717 / 569 | 646 / 547 ms | Fewer merges triggered |
| E. `automerge=0` set before `rebuild` | 1582 | 1945 | 1399 ms | 13 segments at L1; did not help |
| F. Control: built row by row, never rebuilt | 1553 | 1879 | 1454 ms | Steady state at this size |

What the fixture does and does not show:

- **The rebuild leaves pending merges, and the first write pays for them**
  (A: 1633 ms, then 955 and 829 ms, and the structure record shows the merges
  completing). Both `optimize` (B) and bounded `merge` steps (C) remove them.
- **At 22 MB the cost is not separable from steady state.** The never-rebuilt
  control (F) is as slow, because a 1.9 MB trigram write is itself big enough to
  trigger merges. The cost only stands out once the index is large enough that
  the pending merge exceeds what one write would trigger anyway, as at 60k. A
  small fixture can show the mechanism; only 60k shows the size of the effect.
- **Merge work on the write path is a large part of every big capture, rebuild
  or not.** D (`automerge=0`) captures 1.9 MB in 577-801 ms against
  1454-1879 ms for the control. That is a broader finding than the rebuild case,
  and it has to be measured at 60k before anyone acts on it.

### How often a rebuild happens

Since U-E1 (B), a version bump rebuilds only when the FTS schema was recreated,
or when the stored version is below `LastSearchIndexChangeVersion` (3). A
library older than v0.11.0 or a repaired index does it, and an ordinary upgrade
does not. This is a rare path. The cost predates Phase E.

### Options

1. **`'optimize'` right after every rebuild, synchronously.** It removes the
   whole pending merge and leaves one segment. Cost: 48 s at 60k on top of the
   rebuild, on the path that is already the slowest. Not recommended.
2. **Bounded `('merge', N)` steps at idle after a rebuild, until drained.**
   - It removes the same pending merges as `optimize` does for the capture path:
     all levels at or below the pending ones are settled, though it does not
     produce one segment.
   - Each step is a short write transaction: 10-59 ms at N=64 on the small
     fixture, and up to 115 ms at N=256. It interleaves with captures under
     `busy_timeout`, so it never holds the write lock for long.
   - It drains in about 0.5 s total at 22 MB; at 60k the total should be a few
     seconds, unmeasured.
   - Completion is detectable: a step that changes fewer than 2 rows (the
     difference in `total_changes()`) did no work.
   - Fits the existing worker pattern (`EmbeddingWorker`,
     `BackgroundOcrQueue`): start after `InitializeAsync`, stop on shutdown,
     resume next start. No persisted state is needed, because FTS5 records its
     own merge state.
   - No visible change.
3. **Tune `automerge`, `crisismerge`, `usermerge`.**
   - Raising `automerge` (D') makes merges rarer but larger, so it moves cost
     around rather than removing it.
   - Setting `automerge=0` before the rebuild (E) makes the result worse.
   - `crisismerge` (default 16) is the backstop that forces a merge on the write
     path once a level holds that many segments. Lowering it adds write-path
     merges; raising it lets queries slow down as segments pile up.
   - None of these removes the debt alone.
4. **Move all merge work off the capture path: `automerge=0` permanently, plus
   option 2 run continuously at idle.**
   - Captures stop paying for merges. At 22 MB, large captures took 0.6-0.8 s
     against 1.5-1.9 s.
   - Risk: if the idle merger falls behind (the app is never idle, or the worker
     crashes), segments accumulate. Queries slow down until `crisismerge` forces
     a large merge onto a capture: the same cost, later and lumpier.
   - It needs a 60k measurement, and a test that the segment count stays
     bounded under a capture burst.
   - The `automerge` setting persists in `clips_fts_config`, so it outlives a
     downgrade to a build that does not run the idle merger. That matters while
     versions with and without the setting coexist.
   - Larger in scope than the rebuild problem; a separate unit if wanted.
5. **Do the rebuild itself in the background after startup.** **[user decision]**
   - A single `'rebuild'` statement holds the write lock for its whole duration
     (17 s at 5k clips), so captures would time out against the 5 s
     `busy_timeout`.
   - It is only viable as `'delete-all'` followed by batched
     `INSERT INTO clips_fts(rowid, ...) SELECT ... WHERE id BETWEEN ...`
     transactions, resumable by id the way the payload move is.
   - Visible: until it finishes, keyword search misses older clips. Search
     would need to fall back to the in-memory scan the search already uses for terms FTS cannot serve (`HasFtsCompatibleSearchTerm`),
     or a "search index is being rebuilt" notice.
   - Worth doing only together with problem 2's background design, since it
     shares that design's machinery.

### Recommendation

**Option 2: drain pending merges at idle after a rebuild.** It is cheap, has no
visible change, holds the write lock for at most about 100 ms at a time, needs
no new persisted state, and covers both rebuild triggers.

Keep option 4 as a separate, measured proposal; it is the bigger win if the
60k numbers hold. Option 5 only makes sense as part of problem 2's background
migration.

**Unit R1: `FtsMergeDrainer`**
- What: after `InitializeAsync` reports that it rebuilt the index (it already
  traces `rebuild-search-index`), a background worker runs
  `('merge', 64)` steps with a short delay between them, while no capture is in
  flight, until a step changes fewer than 2 rows. It stops cleanly on shutdown
  and resumes on the next start.
- Test strategy:
  - Integration test 1: rebuild, run the drainer, then decode the structure
    record and assert no level is mid-merge. Assert the structure record, not
    the hit counts: a pending merge and a settled one return identical search
    results, the trap `FtsSchemaRepairTests` already documents.
  - Integration test 2: a capture issued while the drainer runs commits within
    `busy_timeout`.
  - Mutants: drainer never started; drainer stops after one step; merge step
    size unbounded (`('merge', -1)`).
  - A 60k confirmation run: first-capture time on a freshly rebuilt seed with
    the drainer, against without it.

## 2. The first start after an upgrade

### Where the time goes

On the final Phase E code, measured at 60k in the 2026-10-28 quiet window:
- plaintext: **73-74 s** (114-118 s before U-E1 (B));
- encrypted: **303-423 s** (471-483 s before).

The progress overlay from U-E1 (C) is in place, so the user sees the stages,
but the app is unusable until the start finishes.

At 5k clips (420 MB), the per-step breakdown before (B) was:

| Step | Time | Notes |
|---|---:|---|
| FTS rebuild | 17.2 s | No longer runs on this bump since (B) |
| Payload move | 5.5 s | |
| Stored-bytes backfill plus the other backfills | 4.2 s | |
| `quick_check` | 0.85 s | |

A per-step breakdown at 60k has not been taken. The first unit below takes it,
because it decides how much each option buys.

Two findings shape the options:

- **The backfills are slow mainly because of the layout E fixes.** Each
  backfill is a full-table `UPDATE ... WHERE` scan. While payloads are still
  inline, before any metadata column, every scan walks each row's overflow
  chain: the F-06 cost (1,375 ms against 10.5 ms measured in the audit). Run
  after the move, the same scans read only small rows.
  - Correctness trap: `BackfillClipPayloadColumnsAsync` sets
    `byte_size = COALESCE(LENGTH(CAST(content AS BLOB)), LENGTH(content_bytes), 0)`
    where `byte_size = 0`. Once the payload has moved, `content_bytes` is NULL,
    so that backfill must read `clip_payloads` instead. A reorder without that
    change silently records zero sizes, which also breaks the size cap.
- **`quick_check` reads every page,** and on an encrypted library every page is
  decrypted. It runs only when structural work is pending, so only on this one
  start. Its 60k cost, plaintext and encrypted, is unmeasured.

### What must hold whatever the option

- **A crash at any point leaves a library that opens and loses nothing.** The
  existing move gives this per batch: one transaction copies, clears and
  records `payload_move_last_id`, and the schema version is written last.
- **Every read sees each clip's payload and icon exactly once,** wherever it
  currently lives.
- **A write to a clip during migration is not overwritten by the migration.**
  This is new for any concurrent option. Today the move upserts with
  `ON CONFLICT(clip_id) DO UPDATE`. If a user edit has already written a fresh
  payload to `clip_payloads`, a later batch would copy the stale inline bytes
  over it.

### Options

**A. Keep the blocking start, make it cheaper.** No behaviour change. Sync and
low-risk.
- Move the payload first, then run the scanning backfills (with the
  `byte_size` fix above), so they scan small rows.
- Skip or narrow backfills a library cannot need: each exists for a specific
  older version, and could be gated on the stored version the way (B) gates
  the FTS rebuild.
- Measure `quick_check` and decide whether this start needs it. It protects the
  rewrite, so dropping it is a risk call.
- Expected gain: the backfill share, minus the move's own cost. It does not fix
  the encrypted 5-7 minutes, which is mostly decrypting and re-encrypting the
  payload pages, so it is not enough alone for 1.0.

**B. Background migration after startup: the app is usable while it runs.**
**[user decision]**
- At startup, run synchronously only what reads need: schema DDL, new tables
  and columns, triggers, and the version-gated steps that are cheap after
  option A.
- The schema version and the payload-move state become separate.
  `schema_version` advances once the cheap steps finish, and
  `payload_move_last_id` plus a `payload_move_done` marker track the move. The
  integrity gate stops treating an unfinished move as "pending structural work",
  or every start during the move pays `quick_check`.
- The app opens, and a background worker runs the existing batches.
- Reads, during the move: every read takes the payload as
  `COALESCE((SELECT content_bytes FROM clip_payloads WHERE clip_id = c.id), c.content_bytes)`
  and the icon as `COALESCE(<app_icons png by hash>, c.source_app_icon)`.
  `ClipSelectColumns`, hydration, OCR's image page and export all need it. The
  fallback goes away in a later version, once every supported library has
  finished the move.
- Writes, during the move: new captures already write the new layout. The move
  must use `ON CONFLICT DO NOTHING` and clear the inline columns only for rows
  it actually copied, so a newer payload always wins. A delete cascades from
  both places (FK plus `clips_payload_ad`). Sensitive purges and retention
  already delete whole rows.
- Size cap: unaffected. `stored_bytes = byte_size` does not depend on where the
  payload lives. Icons are counted once in `app_icons`, plus any still inline,
  until moved. `TotalStoredBytesSql` would need the inline term while the move
  runs.
- Lock contention: each batch is one write transaction (200 rows / 32 MB).
  Encrypted, a 32 MB batch can take seconds, close to the 5 s `busy_timeout`,
  which captures would hit. Batches need a time budget, e.g. 8 MB or 250 ms,
  whichever comes first.
- Crash safety: unchanged per batch, and the worker resumes from
  `payload_move_last_id` on the next start.
- Visible: the app is usable at once, and the overlay is replaced by a small
  "optimizing library" status with progress. Disk and CPU are busy for minutes
  after an upgrade; large-row reads stay at the old speed until the move
  reaches those rows.
- Risk: the largest of the options. Two layouts are live at once, so every
  payload read and write path has to be covered in both. The fallback has to
  stay until a later version removes it.

**C. Lazy migration: move a clip when it is next read or written.**
- On its own it never finishes, so metadata scans keep walking overflow chains
  for every untouched clip, and that is most of the library, which F-06 exists
  to fix.
- It needs B's dual-read fallback and B's write rule anyway.
- Not recommended, except as a small addition to B: moving a clip early when it
  is hydrated.

**D. Chunked migration across sessions: a time budget per start.**
**[user decision]**
- Each start spends up to N seconds on the move behind the overlay, and the
  rest waits for the next start.
- It needs B's dual-read fallback and write rule, but no concurrent writer.
- Crash safety: unchanged.
- Visible: several short blocked starts instead of one long one, and the
  library stays in the mixed layout for days on a rarely restarted machine.
- Safer than B, because nothing runs concurrently, but strictly slower to
  finish. A reasonable fallback if B's concurrency is judged too risky for 1.0.

**E. Copy the whole database into the new layout in one pass.**
- Use `VACUUM INTO` or `INSERT ... SELECT` into a fresh file, then swap it in
  (the restore path already does a crash-safe swap).
- Sequential I/O, and it avoids the freelist churn of the in-place move.
- Needs about twice the disk space while it runs, still blocks the start, and
  re-encrypts every page.
- Not recommended: the in-place move is already bound by page I/O (the
  SQL-only experiment found no gain), and this only adds a whole-file copy.

### Recommendation

**A now, then B, with D as the fallback if B is not accepted.**

- **A** is low-risk, changes nothing visible and should take a large part off
  the plaintext start. Measure first.
- **B** is the only option that makes the 5-7 minute encrypted start invisible.
  It is a user decision because the app is usable on a library that is still
  being migrated.

Units, in order:

- **U1 - 60k first-start breakdown (measurement only).**
  - Trace each gated step at 60k, plaintext and encrypted, using
    `P16_PayloadMigration` with the `[init-timing]` trace captured.
  - Decides how much A buys.
  - Test: none; the evidence goes on the PR.
- **U2 - Option A.**
  - Change: move the payload before the scanning backfills, fix
    `BackfillClipPayloadColumnsAsync` to read `clip_payloads`, and gate each
    backfill on the version it was written for.
  - Test 1: a legacy library with `byte_size = 0` rows comes out with correct
    sizes after the reordered start. This test fails without the `byte_size`
    fix, and is the trap's guard.
  - Test 2: each gated backfill still runs for a library of its version. Extend
    the existing `DatabaseInitializerMigrationTests` fixtures.
  - Mutants: backfill before the move; `byte_size` read from `content_bytes`; a
    backfill gate off by one version.
  - Measure: U1 again.
- **U3 - Option B's read and write rules,** behind no switch, with the move
  still synchronous.
  - Change: the dual-read `COALESCE` on every payload and icon read, and the
    move's `DO NOTHING` plus "clear only what you copied".
  - Test 1: a library stopped mid-move (the existing crash-mid-batch seam)
    reads every clip's payload and icon correctly, through `GetByIdAsync`,
    hydration, export, the OCR image page and search.
  - Test 2: an edit to an unmoved clip, then the move, keeps the edit. This
    fails today with `DO UPDATE`.
  - Mutants: no fallback in hydration; no fallback for icons; the move upserts
    again.
- **U4 - Option B's background worker.** **[user decision]**
  - Change: the move leaves `InitializeAsync` and becomes a worker, separate
    state, a batch time budget, the integrity gate change, and a status line in
    place of the overlay.
  - Test 1: headless, extending `LibraryUpgradeProgressHeadlessTests`. With the
    move held mid-batch, the clip list loads, a capture lands, and the status
    shows progress.
  - Test 2: integration. A capture during a held batch commits within
    `busy_timeout`.
  - Test 3: a crash mid-batch resumes on the next start without
    `quick_check`.
  - Mutants: worker never started; batch time budget ignored; integrity gate
    still triggered by an unfinished move.
  - 60k: time to usable, and the move's duration in the background, plaintext
    and encrypted.
- **(D instead of U4)** if B is declined: a per-start time budget on the
  synchronous move, reusing U3. Test: a library needing three budgets finishes
  over three starts and reads correctly in between.

## Update signing

**User decision (2026-10-28): no paid code signing.** In the user's words,
"signing is money and I don't want to spend it". Update integrity stays the
feed pin to `https://github.com/lurebat/Clipthrough/` (U-C7) plus the security
of the GitHub account and its CI token: an accepted risk under S37.

A free route exists, recorded as an option only, with no action taken:

- **SignPath Foundation** signs open-source releases at no cost
  (https://signpath.org, conditions at https://signpath.org/terms).
- **The certificate is issued to SignPath Foundation, not to the project,** and
  signing runs on their HSM from CI. The publisher shown to users would be
  SignPath Foundation.
- **Conditions:**
  - an OSI-approved licence for all components; Clipthrough is MIT and public;
  - no proprietary components;
  - actively maintained, already released, and documented on its download page;
  - sign only binaries built from your own source;
  - the user is told about any data sent to systems the user did not choose.
- **What would need checking before applying:**
  - the vendored `external/ShareX.ImageEditor`, its licence and whether
    signing a modified fork fits their upstream rules;
  - the bundled semantic model's licence;
  - whether the update check and the AI endpoint meet the data-transfer rule.
    The AI endpoint is user-configured; the update check goes to GitHub.
