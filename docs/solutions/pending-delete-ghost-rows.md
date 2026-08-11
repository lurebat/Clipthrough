---
tags: [viewmodel, paging, delete, undo, ui-state]
version: v0.13.0
severity: p1
status: active
---

# Pending deletes resurrect as ghost rows and shift every later page

## Problem

Deleting a clip and then scrolling far enough to trigger "load more" silently
skipped a clip. With clips 5..1 in the list and clip 5 deleted, paging produced
`[5, 4, 2, 1]` — clip 3 was never shown, and clip 5 was on screen despite having
been deleted.

## Root cause

A delete is optimistic: `SoftDeleteClipsAsync` removes the row from `Clips`,
records it in `_pendingDeletes`, and only writes to the database five seconds
later (`CommitPendingDeletesAsync`) so the user can undo. Three things went
wrong inside that window.

1. **The row came back.** `ApplyRefreshResult` applied whatever the database
   returned, and the database still contained the pending delete. Any refresh
   landing inside the undo window re-added the row the user had just deleted.

2. **The paging offset was a mutable counter.** `UpsertClipItem` did
   `_currentOffset = Clips.Count;` and then
   `HasMoreResults = HasMoreResults || Clips.Count > _currentOffset;` — provably
   dead code, comparing a value against itself. Nothing adjusted `_currentOffset`
   when `SoftDeleteClipsAsync` removed a row, so after the delete committed the
   offset was one too high and the next page started one row too late.

3. **Counting pending deletes that no longer match.** The first repair used
   `Clips.Count + _pendingDeletes.Count` as the offset. That over-counts as soon
   as the user changes the search text during the undo window: the deleted clip
   is no longer part of the result set at all, so adding it inflates the offset
   and skips a row again.

## Solution

- **`ApplyRefreshResult` filters out `_pendingDeletes`** before diffing, so an
  optimistically deleted row cannot reappear.

- **The mutable `_currentOffset` counter is gone.** The offset is derived:

  ```csharp
  private int LoadedResultCount => Clips.Count + _hiddenPendingDeletes.Count;
  ```

- **`_hiddenPendingDeletes`** is the set of pending-delete ids that are genuinely
  part of the *current* result set — i.e. rows the query returns but the list
  deliberately does not show. It is added to in `SoftDeleteClipsAsync` and
  `LoadMoreAsync`, rebuilt from the live result in `ApplyRefreshResult`, and
  cleared in `CommitPendingDeletesAsync`, `UndoDeleteAsync` and
  `CancelAllPendingDeletes`. A pending delete that stops matching the filter
  drops out of the set on the next refresh and stops affecting the offset.

## Prevention

- Anything that removes rows from `Clips` outside `SoftDeleteClipsAsync` will
  desynchronise paging again. Derive the offset; never maintain it by hand.
- `HasMoreResults` must compare `LoadedResultCount` — not `Clips.Count` — with
  `result.TotalMatchingCount`, because the query still counts the hidden rows.
  Comparing the visible rows makes the list claim another page exists and
  re-runs a full search on every scroll near the bottom.
- Any new "hidden but still returned by the query" state (a mute/snooze
  feature, say) needs the same treatment as `_hiddenPendingDeletes`, or the
  offset silently drifts.
- Regression tests, both verified to fail on the pre-fix code:
  - `LoadMore_AfterADeleteCommits_DoesNotSkipARow`
  - `HasMoreResults_IsFalseWhenOnlyAPendingDeleteIsMissing`

  Plus `LoadMore_AfterFilterStopsMatchingAPendingDelete_DoesNotSkipARow`, which
  guards the design of the offset rather than the original defect: it fails
  against a naive `Clips.Count + _pendingDeletes.Count`, not against the
  pre-fix code.

  All use `PagedSearchClipStore`, a decorator that enforces a small page size
  over the real store so paging behaviour is exercised with only a handful of
  clips. Note that a refresh collapses the loaded list back to a single page,
  so a test that needs everything loaded after a refresh must size the page to
  hold it.
- Related, still open: a refresh collapses the loaded list back to a single
  page, so pages already loaded are discarded.
