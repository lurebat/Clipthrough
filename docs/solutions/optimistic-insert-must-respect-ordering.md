---
tags: [viewmodel, search, sorting, filtering, sqlite, rx]
version: v0.13.0
severity: p1
status: active
---

# New clips ignored the sort and the active filters

## Problem

Three symptoms, one cause. With a non-default sort or an active filter:

1. A newly captured clip always appeared at the very top of the list, above the
   pinned clips, no matter which sort was selected.
2. A clip that the active filter excludes - a plain clip while *Favourites
   only* was on, an image while the filter was *Text* - appeared anyway, and
   stayed until the next refresh.
3. Searching with regex, case-sensitivity, wildcards or whole-word matching
   returned results in most-recent order regardless of the selected sort.

Symptom 3 is a separate code path from 1 and 2, but the underlying mistake is
the same: **ordering is defined in SQL, and code elsewhere assumed it knew what
that ordering was.**

## Root cause

Ordering lives in exactly one place, `ClipStoreService.BuildOrderClause`, and
membership lives in `BuildWhereClauses`. Two callers bypassed them.

`SearchAsync` diverts to `SearchInMemoryAsync` when a search uses a flag SQLite
FTS cannot express (`UseRegex`, `CaseSensitive`, `UseWildcard`, `WholeWord`) or
when the term is not FTS-compatible. That in-memory path streamed rows under a
hardcoded most-recent `ORDER BY` instead of `BuildOrderClause(SortOption)`.
Note the divert is gated on `hasSearch`, so the default unsearched list was
never affected - only searches, which is why this survived so long.

`UpsertClipItem` in the view model inserted every captured clip at index 0.
That is right only under the default sort with no filter and no search. Every
arm of `BuildOrderClause` leads with `(c.pinned_at IS NULL), c.pinned_at DESC`,
so index 0 is *above the pinned clips* - a position the SQL can never produce
for an unpinned clip.

## Solution

Symptom 3: use `BuildOrderClause(filters.SortOption)` on the in-memory path.
One line, and it now shares the single definition of ordering.

Symptoms 1 and 2: `TryGetOptimisticInsertIndex` decides whether the correct
position is *knowable without asking the database*. It returns false - deferring
to a real refresh - unless all of:

- `SortOption == MostRecent`, so the newest clip belongs at the top of the
  unpinned run;
- `SearchText` is blank, so no relevance ordering or semantic fusion applies;
- `ClipStructuralFilter.Matches` accepts the clip;
- the clip is **not pinned**.

When it does apply, it walks past the leading pinned run rather than using 0.

`Models/ClipStructuralFilter.cs` is a small predicate mirroring only the
non-textual `BuildWhereClauses` rules: content types, favourites-only,
sensitive-only, pasted-only. Search text is deliberately out of scope, which is
safe because the predicate is only consulted when the search box is empty.

Declined captures go through `RequestDeferredRefresh()`, which throttles a real
refresh at 300 ms when the window is visible, and otherwise just sets
`_isClipListStale` for the next show.

## Prevention

1. **Never mirror the SQL comparer in C#.** It is tempting to compute the exact
   insert position for any sort. Rejected deliberately: it becomes a third copy
   of the ordering rules; SQLite `BINARY` collation compares UTF-8 bytes while
   .NET `CompareOrdinal` compares UTF-16 units, so the two disagree on
   supplementary-plane characters; and it still cannot tell whether the correct
   position falls inside the currently loaded page at all. Decline and refresh.

2. **Pinned clips must be declined.** They order by `pinned_at`, not by
   recency, so re-copying a pinned clip must not move it. Returning an
   optimistic index for a pinned clip reorders the pinned block.

3. **The predicate and the WHERE clause must agree.** Any new filter dimension
   added to `BuildWhereClauses` must be added to `ClipStructuralFilter`, or a
   capture will be shown that the next refresh then removes.

4. **`RequestDeferredRefresh`'s Rx subscription must never terminate.** It is
   the only record that a declined capture is pending. `OnError` is terminal in
   Rx, so the `Catch` lives *inside* the `SelectMany`; letting a fault reach the
   outer `Subscribe` would unsubscribe it for the session, after which
   `RequestDeferredRefresh` pushes into a dead subject and does not even set the
   stale flag. `QueueRefreshAsync` hands every caller the same shared task, so a
   failure raised by an unrelated caller can surface here.

5. **`MarkPastedAsync` also bumps `last_copied_at`.** Most-pasted and
   most-recent are coupled, which matters when building sort fixtures.

## Tests

`ClipStoreServiceTests`:

- `SearchAsync_InMemoryPathHonoursTheSelectedSort` - a theory over all six
  sorts, using the no-search SQL path as the oracle and `CaseSensitive = true`
  to force the in-memory path. Mutation-verified: reverting the one-line fix
  fails 4 of 6 (MostRecent and BestMatching legitimately resolve to the old
  hardcoded clause).
- `SortableClipFixture_ProducesADistinctOrderForEverySortKey` - an anti-vacuity
  guard, and not a theoretical one: it caught a first draft whose largest clip
  was also the most recently pasted, making `LargestFirst` and `MostRecent`
  identical so one theory case proved nothing.

`ClipStructuralFilterTests` - a 32-combination cross product asserting the
predicate agrees with SQLite itself, plus `Matches_IgnoresSearchText` pinning
the boundary. Mutation-verified against four mutants.

`MainWindowViewModelHeadlessTests` - `CapturedClip_IsInsertedBelowThePinnedClips`,
`CapturedClip_UnderANonDefaultSort_IsNotPlacedOnTop`,
`CapturedClip_ThatFailsTheActiveFilter_IsNotShown`,
`RecapturingAPinnedClip_DoesNotReorderThePinnedClips`, and
`DeferredRefresh_SurvivesAFailingRefresh`.

**Watch for vacuous tests here.** The buggy behaviour is *transient* - a later
refresh repairs the list before a naive assertion runs, so the test passes
against the unfixed code. Three tests in this area were vacuous on first write.
Assert over intermediate states by subscribing to `Clips.CollectionChanged`,
not over the settled list. The resilience test had the same problem in a
different form: it caught the sort change's own refresh rather than the
deferred one, so it asserts on the traced error context to prove which path
actually failed.
