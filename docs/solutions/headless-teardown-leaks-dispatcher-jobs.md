---
tags: [testing, avalonia, threading, tooling]
version: v0.13.0
severity: p2
status: active
---

# Headless tests fail in cleanup because work outlives the test

## Problem

Avalonia headless tests fail intermittently with a *cleanup* failure rather
than an assertion. Two signatures, both attributed to a test that is entirely
innocent, and both reported as taking `[1 ms]`:

```
[Test Case Cleanup Failure (...)]: System.InvalidOperationException :
    Cannot get KeyValueStorage on the idle test context
  at Avalonia.Headless.XUnit.AvaloniaTestRunner.<<Run>b__0>d.MoveNext()
```

```
[Test Case Cleanup Failure (...)]: System.InvalidOperationException :
    The calling thread cannot access this object because a different thread owns it.
  at Avalonia.Rendering.DefaultRenderLoop.Add(IRenderLoopTask i)
  at Avalonia.Rendering.Composition.Server.ServerCompositor..ctor(...)
  at Avalonia.Headless.AvaloniaHeadlessPlatform.Initialize(...)
  at Avalonia.Headless.HeadlessUnitTestSession.EnsureIsolatedApplication()
```

The victim varies from run to run, which is why `AGENTS.md` recommends
filtering headless tests out entirely.

## Root cause

`AvaloniaTestRunner.Run` dispatches the test onto the session's dispatcher
thread and then, **after the test body and its `Dispose` have finished**, calls
`Dispatcher.RunJobs()` one more time:

```csharp
return await session.Dispatch(async () =>
{
    var dispatcher = Dispatcher.UIThread;
    var summary = await Run(ctxt);   // test body + IDisposable.Dispose()
    dispatcher.RunJobs();            // <-- xUnit's context is already idle here
    return summary;
}, ...);
```

By then xUnit has retired the test context, so anything queued that reaches
into xUnit throws `Cannot get KeyValueStorage on the idle test context`.

`[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]` makes
this worse. `EnsureIsolatedApplication()` calls `Dispatcher.ResetBeforeUnitTests()`
and rebuilds the whole platform — including a new `Compositor` — for every
test. `Dispatcher.UIThread` is a static binding that is torn down and re-bound
in that window, so a stray *thread-pool* continuation touching it (an Rx
throttle firing, a fire-and-forget database task completing, a posted focus
job) can leave it owned by the wrong thread. The next test's
`ServerCompositor` constructor then fails `VerifyAccess`.

The common ingredient in both signatures is the same: **work that outlives the
test body.**

## Solution

What is fixed: a test harness must tear everything down *before* draining the
dispatcher, not after. Closing a window and disposing a view model both post
their own jobs, so draining first leaves exactly those jobs for the runner's
trailing `RunJobs()`.

```csharp
public void Dispose()
{
    try { Window.Close(); } catch { /* test teardown */ }
    ViewModel.Dispose();
    _scope.Dispose();

    // Posting from inside a posted job is normal, so one pass can leave work
    // behind; a handful of passes settles it.
    for (var i = 0; i < 5; i++)
    {
        try { Dispatcher.UIThread.RunJobs(); } catch { /* test teardown */ }
    }
}
```

Measured on `ClipListFocusHeadlessTests`, which posts a focus-restore job:
**roughly 1 failed run in 8 before, 24 consecutive clean runs after.**

## What is still open

That ordering fix only covers jobs already on the dispatcher queue when the
test ends. It cannot reach work that has not been *posted* yet — a pool thread
still inside a database call or an Rx throttle window will post after any
number of drains. `MainWindowViewModelHeadlessTests` and
`MainWindowHeadlessTests` still flake at a few percent.

Levers measured against the full headless filter, and what they were worth:

| configuration | failures |
| --- | --- |
| `PerTest` (as shipped) | ~4 / 32 |
| `PerAssembly` | 2 / 55 |
| `PerTest` + drain in `TemporaryDatabaseScope.Dispose()` | 2 / 12 |
| `PerAssembly` + that drain | 2 / 30 |

None of them removes it, so none was taken:

- `PerAssembly` trades per-test isolation for a ~3x lower rate, and raises the
  blast radius — a poisoned application takes several tests in the class down
  together, which the runs above show happening.
- Draining inside `TemporaryDatabaseScope.Dispose()` looks right but fires too
  early for the same reason as above, and did not measurably help.

Fixing it properly means the view model owning its background work: a
`CancellationTokenSource` cancelled on `Dispose`, and awaitable handles for the
fire-and-forget `_ = SomethingAsync()` calls, so a test can wait for quiescence
instead of guessing at it.

## Prevention

- Tear down, **then** drain. Never the other way round.
- A headless failure reported as `[Test Case Cleanup Failure]` at `[1 ms]` is
  almost never about the test it names. Look for what the *previous* test left
  running.
- Before blaming a new test for flakiness, measure the suite without it. Here
  the full headless filter failed 3 times in 12 runs *without* the new class
  and once in 20 *with* it — the new tests were not the cause.
- Do not "fix" a flake by changing a knob until you have measured the rate with
  and without it over enough runs to tell a real effect from variance. Three of
  the four configurations above look like wins on a short run.
