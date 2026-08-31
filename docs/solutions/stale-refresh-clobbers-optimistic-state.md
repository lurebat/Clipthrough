---
tags: [viewmodel, refresh, concurrency, ui-state]
version: v0.13.0
severity: p1
status: active
---

# Stale refresh snapshots roll back optimistic clip state

## Problem

Favouriting or pinning several checked clips at once left some rows showing
their *old* state in the list even though the database was written correctly.
The rows corrected themselves only on the next unrelated refresh.

The matching headless test, `SelectAllAndFavoriteSelected_UpdateAllCheckedClips`,
failed only under suite load — it passed when run alone, which is what made the
bug look like test flakiness rather than a product defect.

## Root cause

Two distinct defects, both in the window created by `await`-ing a database
write per clip:

1. **Orphaned view models.** `FavoriteCheckedClipsAsync` and friends captured
   `ClipItemViewModel` references from `GetCheckedOrSelectedClips()` and then
   mutated them *after* each `await`. `ApplyRefreshResultIncremental` disposes
   and replaces any VM whose `ClipEntry` changed, so a refresh landing mid-loop
   left the command writing to detached, already-disposed objects.

2. **Stale snapshots winning.** This is the one that actually broke the test.
   `PerformRefreshAsync` reads its snapshot on a background thread and applies
   it later on the UI thread. `ClipItemViewModel.SetFavoriteState` mutates
   `Clip.IsFavorite` in place, and `ClipsAreMateriallyEqual` compares
   `IsFavorite` / `PinnedAt` / `IsPasted` / `IsSensitive`. So a snapshot taken
   *before* the write, applied *after* the optimistic mutation, looked like a
   change — and the diff dutifully replaced the row with the pre-write entry,
   silently reverting the UI.

Sequence that produced the observed `clip 2 = not favorite, clip 1 = favorite`:

```
refresh: SELECT  (both clips not favorite)      <- snapshot taken here
command: write clip 2 = favorite
command: clip2Vm.SetFavoriteState(true)          <- Clip.IsFavorite now true
refresh: apply -> clip 2 differs from snapshot -> REPLACED with not-favorite
command: write clip 1 = favorite
command: clip1Vm.SetFavoriteState(true)          <- still live, survives
```

## Solution

**`ApplyToLiveClip(long clipId, Action<ClipItemViewModel>)`** re-resolves the
clip by id from the live `Clips` collection (via `IndexOfClip`) before applying
any post-`await` state change. Never mutate a `ClipItemViewModel` captured
before an `await`.

**`RunClipMutationAsync(Func<Task> write)`** wraps every clip-state write to the
store and maintains two counters:

- `_pendingClipMutations` — non-zero while a write is in flight.
- `_clipMutationVersion` — incremented once per completed write.

`PerformRefreshAsync` samples both immediately before building its request and
re-checks them after `SearchClipsAsync` returns. If a write was in flight at
either end, or the version moved, the snapshot is stale and the search is
re-run. It is bounded to `maxAttempts = 4`; on the final attempt the result is
applied anyway and `_hasQueuedRefresh` is set so a follow-up refresh converges.

`ToggleFavoriteStateAsync` also switched from `ReferenceEquals(SelectedClip, clip)`
to comparing ids, for the same orphaning reason.

## Prevention

- Route **every** clip-state write through `RunClipMutationAsync`. Calling
  `_clipStoreService.Set…` directly reintroduces the rollback, because the
  refresh has no way to know the snapshot went stale. Note that the store now
  hops off the caller itself, so a direct call *looks* correct and is not —
  the counters, not the thread, are what this method is for.
  `clip-mutation-version-stops-moving` fails the regression test below if the
  version counter stops moving.
- Any new field added to `ClipsAreMateriallyEqual` widens this hazard — it is
  the predicate that decides whether a refresh replaces a row.
- Regression test: `FavoriteCheckedClips_SurvivesRefreshSnapshotTakenBeforeTheWrite`
  uses `GatedSearchClipStore` to park a search after it has read the database
  but before the caller applies the result, which reproduces the window
  deterministically instead of relying on load. Verified to fail on the
  pre-fix code with "clip N was rolled back to not-favorite by a stale refresh".
- Headless ViewModel tests that assert on `SelectedClip` after emitting a
  capture must call `viewModel.SetMainWindowVisible(true)` first;
  `ApplyCapturedClipOptimistically` deliberately skips selection while hidden.

## Follow-up: the retry must not become a spin

The first version of the staleness test also treated "a write was in flight
when this attempt started" as a signal in its own right, and
`ProcessQueuedRefreshesAsync` re-entered `PerformRefreshAsync` with no delay.
A bulk operation writes one clip at a time and so holds `_pendingClipMutations`
above zero for its whole duration — which meant four full searches plus a full
list diff, repeated back to back, for as long as the bulk edit lasted.

That signal is redundant. `RunClipMutationAsync` increments
`_clipMutationVersion` **before** decrementing `_pendingClipMutations`, so a
write that finishes during the search always moves the version, and one still
running is caught by the pending count. Keep that ordering: reversing it opens
a hole where a completing write is invisible to both checks. A stale retry now
also waits `StaleRefreshRetryDelay` before re-reading.

The check runs on a pool thread while the snapshot is applied after a hop to
the UI thread, so it is re-checked once more inside that lambda — a write
completing in the gap posts its own UI update, which can be ordered first and
would then be rolled back by the older snapshot. The re-check is skipped when
the loop deliberately gave up and applied a knowingly stale result, which would
otherwise never converge.
