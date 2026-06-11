---
tags: [sqlite, workers, storage, crash-safety]
version: v0.13.0
severity: p0
status: active
---

# DatabaseMaintenanceScope: worker quiesce ordering for whole-DB operations

## Problem

Whole-database operations — rekey (SQLCipher password change), storage path
move, backup, and restore — require exclusive access to the database file.
Three background workers (`ClipboardMonitorService`, `BackgroundOcrQueue`,
`EmbeddingWorker`) hold open SQLite connections. Without quiescing them first:

- `ClearAllPools` does not close connections held by running worker loops,
  leaving the file locked during `File.Move` or SQLCipher rekey.
- A path move that restarts workers before updating `Current.DatabasePath`
  causes workers to reopen the old (now deleted) path, creating an empty DB.
- Partially-completed operations left the app in an inconsistent state when
  an exception occurred mid-operation.

## Root cause

Each whole-DB operation was implemented ad-hoc, with inconsistent stop/start
sequences and no guarantee of cleanup on failure. Connection pool clearing was
not always paired with a second clear after the operation body (the body can
open additional connections).

## Solution

`DatabaseMaintenanceScope` is a disposable RAII scope that wraps every
whole-DB operation:

```csharp
await using var scope = await DatabaseMaintenanceScope.EnterAsync(
    _clipboardMonitorService, _backgroundOcrQueue, _embeddingWorker);
// ... whole-DB operation ...
// DisposeAsync restarts workers automatically, even if the body throws.
```

**EnterAsync stop order** (must be this order — monitor first so no new
captures arrive while OCR/embedding drains):
1. `monitor.Stop()` — synchronous; no new captures after this point.
2. `ocrQueue.StopAsync()` — async drain.
3. `embeddingWorker.StopAsync()` — async drain.
4. `SqliteConnection.ClearAllPools()` — releases all pooled connections.

**DisposeAsync restart order** (reverse of stop, pool-clear first):
1. `SqliteConnection.ClearAllPools()` — in case the operation opened new
   connections that the body left open.
2. `monitor.Start()`
3. `ocrQueue.Start()`
4. `embeddingWorker.Start()`

**Null-safety:** any worker reference may be `null` (test context, non-Windows).
The scope skips null workers; `ClearAllPools` always runs.

**Path-move ordering:** the scope must remain alive until after
`Current.DatabasePath` has been updated to the new path. Restarting workers
before flipping the path caused them to reopen the old (deleted) file.

## Prevention

- Never perform `File.Move`, `SqliteConnection` rekey, backup WAL-truncate, or
  restore without wrapping in `DatabaseMaintenanceScope.EnterAsync`.
- The path-move code path ends the scope *after* persisting the new path, not
  before.
- `DatabaseMaintenanceScopeTests` verifies that already-stopped workers are
  restarted if a later worker stop throws during scope entry.
- If a new whole-DB operation is added, reuse the scope; do not hand-roll stop
  sequences.
