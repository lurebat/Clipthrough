---
tags: [sqlite, threading, storage, ui, viewmodel]
version: v0.16.2
severity: p1
status: active
---

# Storage calls hop off the caller, so callers must not

## Problem

SQLite has no asynchronous I/O. Microsoft says so directly for the provider
this app uses:

> SQLite doesn't support asynchronous I/O. Async ADO.NET methods will execute
> synchronously in Microsoft.Data.Sqlite. **Avoid calling them.**
>
> — <https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async>

So `await connection.OpenAsync()` and friends return an already-completed task
having done the work inline. A `ClipStoreService` method awaited from a UI-thread
command handler ran start to finish on the UI thread and froze the window for
the length of the query.

The fix used to be the caller's job: **38** call sites across
`MainWindowViewModel` (19), `App.axaml.cs` (14) and `ClipboardMonitorService` (5)
each wrapped their own `Task.Run`. The invariant survived only as a prose rule
in `.github/copilot-instructions.md`, which a newly added call site could ignore
in silence — the failure mode is a UI freeze in one command, not a test failure.

## Root cause

The blocking is a property of the implementation, but the mitigation lived at
the call sites. Nothing connected the two, so the rule could only be enforced by
someone remembering it.

## Solution

Each of the 37 `IClipStoreService` methods on `ClipStoreService` is a one-line
wrapper over a private `*CoreAsync` body:

```csharp
public Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default)
    => RunOffCallerAsync(ct => CaptureCoreAsync(request, ct), cancellationToken);
```

Callers just `await`. The four internal self-calls
(`GetByIdAsync` twice, `ApplySensitivityAsync`, `ApplyMaintenanceAsync`) go to
the `*CoreAsync` form so the hop is never paid twice — this matters most for
`InsertClipAsync` -> `ApplyMaintenanceAsync`, which is reached **while an outer
connection is still open**.

### Why this is permanent, not a workaround

`Microsoft.Data.Sqlite.Core` **10.0.11** is current, and the quote above is
current guidance, not a known gap. The constraint sits below the .NET binding —
SQLite is an in-process C library whose default VFS makes blocking syscalls — so
no provider (`System.Data.SQLite`, `sqlite-net`, Devart) and no embedded engine
(LiteDB, Realm, DuckDB) removes it. Engines with real async I/O are
client-server, which is absurd for a clipboard manager. **Do not "clean this up"
after a package upgrade.**

### Why the ~264 async ADO calls inside the service are left alone

The same quote says to avoid the async ADO methods, and the bodies still call
them. That is deliberate. Once a body runs on a pool thread, their fake
asynchrony costs one allocation and no thread hop — nothing a user can observe.
Converting them would churn the two most safety-critical files in the repo for
zero behavioural gain. The quote is an argument about *which thread the work
happens on*, and the hop already settles that.

### `ConfigureAwait(false)` is deliberately absent

Inside `Task.Run`, continuations resume on the thread pool, where
`SynchronizationContext.Current` is null and the scheduler is
`TaskScheduler.Default`. There is nothing to capture, so it would be a
provable no-op. The wrappers are expression-bodied and contain no `await` at all.

## What the hop does NOT cover

- **COM clipboard reads** (`Clipboard.GetDataAsync`) are UI-thread-affine and
  must stay there. Offload only the database portion.
- **Other services.** `ISensitivityService` and `ISearchHistoryService` use their
  own connection factory and block exactly the same way. A block that mixes them
  with a store call still needs a `Task.Run` around the uncovered work — see the
  settings-save path in `MainWindowViewModel`.
- **CPU work following a store call.** Awaiting an already-completed task resumes
  *inline on the caller*, so a fast store call does not guarantee what follows it
  is off the UI thread. `SearchClipsAsync` wraps `ApplySemanticFusionAsync` for
  exactly this reason; the comment there says so, or a later reader will delete
  it as redundant.

`RunClipMutationAsync` still exists and still must be used for clip-state
writes. Its `Task.Run` is gone, but its counters — and the order they move in —
are what prevent a stale refresh snapshot rolling the UI back. See
`stale-refresh-clobbers-optimistic-state.md`.

## Prevention

`Clipthrough.Tests/Integration/ClipStoreThreadingTests.cs` holds two tests that
are deliberately paired, because neither is sufficient alone:

- `EveryStoreMethod_HandsItsBodyToTheThreadPool` covers all 37 methods but
  asserts on *shape*: each must delegate to `RunOffCallerAsync` **and** carry no
  `AsyncStateMachineAttribute`. The second half is load-bearing — `IlCallScanner`
  follows an async method into its state machine, so
  `async Task Foo() { await Task.Run(...); /* more inline work */ }` satisfies a
  bare "calls Task.Run" scan while still running its tail on the caller.
- `StoreMethods_DoNotOpenTheirConnectionOnTheCallingThread` asserts the real
  thread for eight representative methods. `SqliteConnectionFactory` reads
  `IStorageOptionsService.Current` once per connection on the thread that opens
  it, which is an observation point *inside* the body needing no production
  change. The call is made from a purpose-created thread, which is never a
  thread-pool thread, so the comparison is decidable rather than a coincidence.

Stated limits: the first proves shape, not that the whole body is off-thread;
the second proves the thread only at connection-open, for 8 of 37.

Both were run against the pre-change tree and **failed** there — all eight
reported the connection opening on the calling thread — which is stronger
evidence than a mutant, because it is the real revert rather than a simulated
one. Mutants `clip-store-runs-inline` and
`clip-store-method-reverts-to-inline-async` guard the helper and a single-method
partial revert respectively.
