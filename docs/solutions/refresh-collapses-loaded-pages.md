---
tags: [viewmodel, paging, search, refresh, ui-state]
version: v0.13.0
severity: p1
status: active
---

# A refresh collapses the list back to a single page

## Problem

Scroll far enough to load several pages of history, then do anything that
touches a clip - copy something, let an OCR job finish, wait for periodic
maintenance - and the list snapped back to the first 200 rows. Everything the
user had scrolled in was gone, with no visible cause.

## Root cause

`PerformRefreshAsync` is the single funnel for every list refresh: filter
changes, data-change refreshes and the periodic pass all reach it through
`RefreshAsync()` -> `QueueRefreshAsync` -> `PerformRefreshAsync`. It built its
query as `BuildFilters(offset: 0)`, and `BuildFilters` hardcoded
`Limit = PageSize` (200). So a refresh only ever re-read the first page.

`ApplyRefreshResultIncremental` then diffs the incoming list against `Clips`
and removes anything the result does not contain. With 200 rows incoming
against 1000 loaded, it removed 800. Paging depth lived only in
`LoadMoreAsync`, which pages with `BuildFilters(LoadedResultCount)`; nothing
carried that depth back into a refresh.

## Fix

`BuildFilters` takes an optional `limit`. A refresh re-reads to
`Math.Max(PageSize, LoadedResultCount)` - but only while the query is
unchanged. When the query changes, the limit falls back to `PageSize` so a new
search starts at one page.

## Invariants

1. **The depth must reset when the query changes.** Re-reading deep
   unconditionally makes the depth ratchet upward and never shrink: every
   subsequent search inherits the deepest page count the user ever reached, and
   each keystroke-triggered search pays for it.

2. **`_lastRefreshFilters` must be recorded where the result is applied, not
   where the request is built.** `LoadedResultCount` is derived from `Clips`,
   which only changes on apply. `PerformRefreshAsync` retries in a loop while a
   clip write is in flight, and can discard a result and requeue. Recording at
   build time meant a retry compared the new query against itself, concluded
   "unchanged", and re-armed the deep read for a query whose reset never ran -
   so the reset silently failed whenever a write straddled a query change.

3. **`ClipSearchFilters` is a plain `sealed class`, not a record.** It has no
   structural equality, so the comparison is the explicit `SameResultSet`
   helper. Any new result-affecting field must be added to it, or a query
   change will be misread as "unchanged".

4. **Paging depth must not leak into unrelated knobs.** The semantic fusion
   candidate pool was `filters.Limit * 2`; once a refresh could pass a large
   limit, the pool grew with paging depth and hydrated proportionally more
   candidate rows on every capture. It is clamped to one page
   (`Math.Min(filters.Limit, PageSize)`) because it is a recall knob, not a
   depth.

## Tests

`MainWindowViewModelHeadlessTests`:

- `Refresh_KeepsLoadedPages_ButANewSearchResetsThem` - seeds 201 clips so real
  paging is exercised (the view model's `PageSize` is a const 200, so a fake
  page-size cannot stand in for it). Mutation-verified in both directions: a
  fixed page size fails it at 201 vs 200, an unconditional deep re-read fails it
  at 200 vs 201.
- `Refresh_ResetsPaging_EvenWhenAClipWriteStraddlesTheQueryChange` - holds a
  clip write open via `BlockingWriteClipStore` so the refresh takes its retry
  path, then changes the query. Defends invariant 2.