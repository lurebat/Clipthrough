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

## Work that outlives the test

Clearing the pool is not enough when something reopens the database after the
clear: the delete fails again and the directory still leaks. A full headless
run with the fix above still left five directories, and three pieces of
`MainWindowViewModel` work nobody awaited caused them:

- A save from the welcome screen starts the database in the background
  (`DatabaseStartupTask`) and returns.
- A database start, and a save with the database open, end with a maintenance
  pass that reloads the rules and refreshes the list.
- A database start runs the daily backup on a pool thread, which copies the
  library into `backups\`.

The view model's `Dispose` is synchronous, so it cannot wait for them. The
view model keeps maintenance passes and the backup in `MaintenanceTask`, and
tracks other background database work separately in `BackgroundDatabaseTasks`.
Both are internal test seams; the app still does not await them.
`TemporaryDatabaseScope` has a `BeforeDelete` hook, and
`HeadlessStartup.DrainDatabaseWorkBeforeDelete` sets it to wait for
`DatabaseStartupTask`, `MaintenanceTask` and in-flight `BackgroundDatabaseTasks`
while pumping the UI thread. The wait is bounded at 30 seconds. `CreateViewModel` in
`MainWindowViewModelHeadlessTests` and `MainWindowTestHarness.Create` install
it. It runs from the scope's `Dispose`, after the view model and window are
torn down, which is the order
`headless-teardown-leaks-dispatcher-jobs.md` requires.

The fix was not to lengthen `TryDelete`'s retries. That would only move the
race, and on an empty library most of this work finishes inside them anyway.
That is also why the regression tests slow the maintenance pass and the backup
on purpose: without that, a scope that does not wait still passes.

When measuring a leak like this, run the mutation check before the
measurement, not between. `Invoke-MutationCheck.ps1` builds into the same
`artifacts\bin` and leaves the last mutant's assembly there, so a later
`--no-build` run tests the mutant.

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
- Background database work a view model starts and does not await must be
  tracked so tests can drain it before deleting their database. Maintenance
  passes and the startup backup use `MaintenanceTask`; work started by
  `StartBackgroundServices` uses `BackgroundDatabaseTasks`.
  The `MainWindowViewModelHeadlessTests.TempCleanup.cs` tests cover the
  database start, the maintenance pass, the daily backup and background-service
  work. Their mutants (`headless-scope-skips-*`, `startup-backup-untracked`,
  `background-ocr-backlog-untracked`) prove each guarded path fails without
  its wait.
