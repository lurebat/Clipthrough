# Copilot Instructions for Clipthrough

## SQLite threading rules

`IClipStoreService` calls from ViewModel command handlers run on the UI thread
(via ReactiveCommand), and SQLite has no asynchronous I/O — Microsoft's guidance
for this provider is "Avoid calling" the async ADO.NET methods, because they
execute synchronously. **`ClipStoreService` therefore moves its own body to the
thread pool.** Just `await` it; do not wrap a store call in `Task.Run()`.

That is enforced, not merely documented: `ClipStoreThreadingTests` fails if any
of the 37 methods stops delegating to `RunOffCallerAsync`, or becomes an `async`
method again.

Two things this does *not* cover, and which still need their own `Task.Run`:

- **COM clipboard reads** (e.g. `Clipboard.GetDataAsync`) MUST stay on the UI
  thread — they are UI-thread-affine, so offload only the DB portion.
- **Other blocking work in the same block.** `ISensitivityService` and
  `ISearchHistoryService` use their own connection factory and are *not* covered
  by the store's hop, so a block that mixes them with a store call still needs
  wrapping. Wrap the uncovered work, not the store call.

Note that `await`ing an already-completed task resumes inline on the caller, so
"it follows a store call" is not on its own a reason CPU work is off the UI
thread.

When fixing a UI-freeze bug, **audit all call sites** of the affected service
method first (`grep _clipStoreService\. MainWindowViewModel.cs`). Fix every
occurrence in one pass rather than fixing only the reported case.

## SQLite concurrency

- WAL mode is enabled in `DatabaseInitializer` and is persistent per-database.
- `busy_timeout=5000` is set per-connection via a `StateChange` event handler
  in `SqliteConnectionFactory` — it is NOT persistent and must be applied every
  time a connection opens.
- SQLite supports only **one writer at a time**. Background workers (embedding,
  OCR) compete for the write lock with UI-triggered operations. Keep write
  transactions short and avoid holding them across async awaits.

## Service initialization order

`EmbeddingWorker.Start()` and `BackgroundOcrQueue.Start()` must be called
**after** `DatabaseInitializer.InitializeAsync()` completes and after the
encryption password is set (when the DB is encrypted). Starting workers before
the password is configured causes "file is not a database" errors. The correct
call site is inside `StartDatabaseAsync`, not in `App.axaml.cs` startup.

## Windows build quirks

- The running `Clipthrough.exe` locks its own DLL. Kill the app before
  rebuilding, or the build will fail silently or produce stale output.
- `obj/` file locks can accumulate from prior test runs. If you see `MSB3492`
  errors ("Could not read existing file"), run:
  ```
  Remove-Item -Recurse -Force .\Clipthrough.Tests\obj
  ```
  then rebuild.
