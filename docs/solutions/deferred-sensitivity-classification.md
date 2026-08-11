---
tags: [security, sensitivity, capture, storage, migration]
version: v0.13.0
severity: p0
status: active
---

# Deferred sensitivity classification could never run

## Problem

`CaptureFastAsync` writes a clip and deliberately skips the sensitivity scan so
the clipboard hook returns quickly; the monitor finishes the job afterwards in
`EnrichCapturedClipAsync`. If that second step never ran — the app was closed or
crashed in the window between them, or enrichment threw — the clip stayed
unclassified **forever**. Nothing retried it.

An unclassified credential is not a cosmetic problem. It renders in plaintext,
is exempt from the shorter sensitive-clip retention, appears in ordinary search
results, and is eligible for the embedding worker, which sends content to an
external model.

## Root cause

"Not yet classified" and "classified as not sensitive" were the same state:
`is_sensitive = 0`. With no way to tell them apart there was nothing to retry
and no way to gate anything on classification having happened.

## Solution

A new nullable column makes the distinction explicit:

```sql
sensitivity_scanned_at TEXT   -- NULL = never classified
```

- `CaptureFastAsync` leaves it `NULL`; `CaptureAsync` (which scans inline)
  stamps it.
- The duplicate-hash `UPDATE` uses
  `COALESCE($sensitivityScannedAt, sensitivity_scanned_at)` so re-copying an
  already-scanned clip never erases the stamp, and re-copying an unscanned one
  does not fabricate a stamp it did not earn.
- `ApplyPendingSensitivityAsync` classifies the `NULL` backlog at startup. A
  per-clip failure leaves the marker `NULL`, so the next launch retries it
  instead of silently giving up.
- `EmbeddingEligibilityClause` gained `AND sensitivity_scanned_at IS NOT NULL`,
  so unclassified content cannot reach the embedding model.
- The migration backfills existing rows with
  `COALESCE(captured_at, first_copied_at)`. Without a backfill an upgrade would
  re-scan the entire library and stall embedding for every existing clip.

Enrichment failures were also raised from `TraceWarning` to `TraceError`, since
they are the trigger for exactly this state and were previously invisible among
routine warnings.

## Prevention

- Any new consumer of clip content must gate on
  `sensitivity_scanned_at IS NOT NULL`, not on `is_sensitive = 0`. The latter is
  true for content nobody has looked at yet.
- Any new capture path must either scan inline or leave the marker `NULL` so the
  startup pass picks it up. A path that stamps the marker without scanning
  permanently hides its clips from the retry, and a path that scans without
  stamping (as `CaptureBatchAsync` originally did) excludes everything it writes
  from embedding until the next launch and then makes the startup pass rescan
  the lot one clip at a time.
- Regression tests in `ClipStoreServiceTests` cover: fast capture leaves the
  marker unset, the startup pass sets it, a failing scan leaves it unset for the
  next attempt, an unscanned clip is not embedding-eligible, and a duplicate
  copy does not erase an existing stamp. Verified to fail on the pre-fix code by
  removing the eligibility gate and by always stamping on insert.
- Still open: `ApplySensitivityAsync` scans clip content but not `ocr_text`, so
  a credential visible only inside a screenshot is not classified.

## Test fixtures for this area

Do **not** paste a secret-shaped literal copied out of tool output. Tool output
redacts such values, so what gets copied is the mask, which matches none of the
built-in rules and makes the test silently assert nothing. Write an obviously
synthetic fixture that a real rule matches, and assert that it does:

```csharp
private const string SyntheticSecretText = "password = NOT-A-REAL-CREDENTIAL";
```

`SensitivityService.Scan` also needs `await ReloadAsync()` first when a test
calls it directly — the capture paths load rules for themselves, so a direct
call can observe an unloaded rule set. And `GetByIdAsync` uses lazy hydration:
it populates `IsSensitive` but not `SensitivityMatches`.
