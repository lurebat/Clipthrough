---
tags: [testing, sqlite, temp-files]
version: unreleased (after v0.17.1)
severity: p1
status: active
---

# Test temp databases leak through the connection pool

## Problem

`%TEMP%\Clipthrough.Tests` grew to 29 GiB on one dev machine: 41,775
per-test directories, each with a `clipthrough.db` (22 GiB in total), its
`-wal` (6.4 GiB) and `-shm`. Every test that opened a database through
`TemporaryDatabaseScope` left its directory behind, and nothing reported a
failure. A single `SensitivityRebuildTests` run leaves about 156 MB behind.

## Root cause

Microsoft.Data.Sqlite pools connections by default. Disposing a pooled
connection returns it to the pool with the file still open, so the
`Directory.Delete` in `TemporaryDatabaseScope.Dispose` threw `IOException` on
Windows. Dispose swallowed the exception, so every leak was silent.

Most other database fixtures called `SqliteConnection.ClearAllPools()` before
deleting. This one, which about 220 tests use, did not.

## Solution

`Clipthrough.Tests/TestTempDirectories.cs` owns the root:

- `CreateDirectory()` creates a GUID-named directory under the root.
- `TryDelete(path)` clears the pool, then deletes with a few short retries
  for a scanner or indexer that has a file open. It is best effort, because a
  teardown failure must not fail the test that owned it.
- A module initializer sweeps the root once per test run on a background
  thread. It deletes directories that are directly under the root, named as
  a 32-hex-digit GUID, and older than 24 hours by both creation and last-write
  time. This clears what earlier runs leaked and anything a crashed run leaves.
  The age guard keeps it away from a run in progress in another worktree. The
  name guard keeps it away from anything the tests did not create.

`TemporaryDatabaseScope`, `SqliteErrorsTests` and
`SettingsLegacyMirrorFirstRunTests` create and delete their directories
through it. The `mirror-` prefix the last one used put its leftovers out of
the sweep's reach.

Clearing the pool globally in teardown is safe only because the test assembly
disables parallelization (`AvaloniaHeadlessSetup.cs`). If that ever changes,
one test's teardown would close another test's pooled connections.

## Prevention

- Create per-test directories with `TestTempDirectories.CreateDirectory()`
  and remove them with `TestTempDirectories.TryDelete()`. Do not hand-roll
  `Path.GetTempPath()` plus `Directory.Delete` in a fixture.
- Never swallow the exception from deleting a SQLite file without first
  clearing the pool. In shipping code, a move or delete of a database, `-wal`
  or `-shm` goes through `DatabaseMaintenanceScope`, an explicit
  `ClearAllPools()`, or a `pooling: false` connection, and failures are traced.
- `TestTempDirectoryCleanupTests` checks that a scope which wrote to its
  database leaves no directory, and that the sweep removes only stale
  GUID-named directories. Three mutants in `mutants.json` (`test-temp-*`)
  prove it fails when the pool clear, the age guard or the name guard is
  removed.
