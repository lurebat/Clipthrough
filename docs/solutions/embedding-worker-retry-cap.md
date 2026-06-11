---
tags: [search, workers, sqlite, embeddings]
version: v0.13.0
severity: p1
status: active
---

# Embedding worker retry cap and processing-row orphan prevention

## Problem

Two related failure modes in `EmbeddingWorker`:

1. **Poison clip CPU spin** — A clip that consistently caused inference errors
   (malformed text, ONNX anomaly) was re-claimed every 30 seconds for all
   time, pinning the worker and competing for the SQLite write lock.

2. **'processing' row orphans** — When `EmbedBatchAsync` returned a different
   number of vectors than inputs (inference anomaly), the claimed rows were
   left with `embedding_status = 'processing'`. `ClaimPendingEmbeddingsAsync`
   never re-offers `'processing'` rows, so they were invisible to the worker
   until the next app restart.

## Root cause

`ClaimPendingEmbeddingsAsync` had no upper bound on how many times a clip
could be claimed. Any unrecoverable error left the door open for infinite
re-tries. The vector count mismatch path silently returned 0 without flagging
the claimed rows, orphaning them in `'processing'`.

## Solution

**Retry cap (`embedding_attempts` column, schema v4):**

`SetEmbeddingFailureAsync` increments `embedding_attempts` on each failure.
`ClaimPendingEmbeddingsAsync` gates claims with:
```sql
AND (embedding_status = 'failed' AND embedding_attempts < $maxAttempts)
```
where `MaxEmbeddingAttempts = 3` (constant in `ClipStoreService`). After 3
failures the clip is permanently excluded from processing without operator
intervention. `MarkAllEmbeddingsForRerunAsync` resets the counter, giving all
clips a fresh set of attempts when the model is replaced or upgraded.

**Processing orphan fix (vector count mismatch):**

`EmbeddingWorker.ProcessOnceAsync` now calls `FlagBatchFailedAsync` on
vector-count mismatch, releasing the claimed rows via `SetEmbeddingFailureAsync`
(which increments the bounded counter) rather than leaving them stuck. The
comment in the code cites issue #13.

**Missing model file:**

`FileNotFoundException` from `EmbedBatchAsync` sets `_modelMissing = true`,
halting claim attempts without flagging the clips as failed (they are not at
fault). The flag is cleared after the idle period so the worker resumes once
the ONNX file is restored.

## Prevention

- `FlagBatchFailedAsync` is the correct call for any batch-level failure;
  never return 0 from `ProcessOnceAsync` after a successful claim without
  also flagging the batch.
- `MaxEmbeddingAttempts` is intentionally not configurable — if you change it,
  also check whether existing `embedding_attempts` column values need a migration.
- `EmbeddingWorkerTests` covers: normal processing, inference error idles (not
  fast-loops), vector count mismatch flags-and-idles, and model-missing halts.
- When adding new failure modes to the worker, always decide up-front: does
  this failure belong to the *clip* (flag via `SetEmbeddingFailureAsync`) or
  to the *environment* (idle without flagging)?
