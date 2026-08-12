---
tags: [testing, tooling, process]
version: v0.13.0
severity: p1
status: active
---

# Regression tests that cannot fail

## Problem

Three tests written during a review pass to defend three specific fixes all
passed against the exact bugs they were written to catch. Each looked
reasonable, each executed the code under test, and each proved nothing.

This is worse than having no test. A missing test is a known gap; a vacuous one
is a false claim of safety, and it survives every future refactor because
nothing ever makes it red.

Coverage does not detect this. `coverlet.collector` is already referenced here,
and all three vacuous tests had *full* coverage of the lines they failed to
defend. Coverage measures execution; the missing property is discrimination.

Stryker.NET, the usual .NET answer, does not work in this repo: it is unreliable
under xUnit v3 / the Microsoft Testing Platform, where mutations fail to activate
and mutants report "Survived" wholesale. The documented workaround is downgrading
to xUnit v2, which is not worth doing to ~370 tests. Revisit if MTP support lands.

## Root cause

A test is only a guard if some plausible wrong version of the code makes it red.
Nothing in the writing process checks that, so four distinct mechanisms each
produced a test that was green either way.

1. **Self-referential oracle.** An equivalence test asserted the new sort matched
   the old sort. Reverting to the old implementation satisfies it by definition.
2. **Non-discriminating proxy.** A test asserted on `EXPLAIN QUERY PLAN` output.
   For the `Alphabetical` sort that output is byte-identical with and without the
   optimisation, so the assertion passed against a complete revert.
3. **Wrong-event assertion.** An async test waited for *an* error to be traced. A
   different, unrelated operation errored first and satisfied it.
4. **Stale build.** A mutant was verified manually, but the restore handed back
   an older file timestamp, MSBuild skipped the rebuild, and the "verification"
   ran the pre-mutation assembly.

## Solution

Break the fix on purpose and watch the test fail, before committing. That is the
only check that distinguishes a real guard from a vacuous one.

`Clipthrough.Tests/Mutation/` automates it. Each entry in `mutants.json` names a
file, an exact anchor string, a replacement, and the test filter that must go
red. The harness applies the mutant, rebuilds, runs that filter, requires
failure, and restores the file byte-exactly in a `finally`.

```
pwsh Clipthrough.Tests\Mutation\Invoke-MutationCheck.ps1
pwsh Clipthrough.Tests\Mutation\Invoke-MutationCheck.ps1 -Id alphabetical-sort-index-dropped
```

Four design points, each of which came from a real failure:

- **Four outcomes, not two.** `dotnet test` also exits non-zero for compile
  errors and for filters matching no tests, so both must classify as
  `INCONCLUSIVE`, never as a kill.
- **`STALE` is a hard failure.** If a mutant's anchor no longer matches exactly
  once, the run tested nothing — which recreates the false-confidence problem one
  level up. Fail loudly instead of skipping.
- **Force the timestamp after restoring.** `(Get-Item $p).LastWriteTime = Get-Date`.
  Without it MSBuild can skip the rebuild and run the mutant assembly against the
  *next* mutant's test.
- **Restore on crash.** The harness writes a pending-restore marker before
  mutating and replays it on startup, so an interrupted run can never leave
  broken production code in the tree.

## Prevention

The four mechanisms above are restated as rules in `AGENTS.md` § Testing
expectations. Beyond following them: any test written to defend a specific fix
gets a mutant in `mutants.json` at the same time, so the proof is repeatable
rather than a one-off manual check.

### Where mutants pay off most

An audit of seven mutants against high-risk areas killed six. The one survivor
was `SensitivityService`, which had no test file at all — and writing those tests
immediately surfaced two real defects (an invalid regex persisted before it was
ever compiled, and unbounded backtracking on the capture path).

So aim mutants at code with *no* tests before aiming them at code whose tests
merely look thin.

## Related

- `Clipthrough.Tests/Mutation/README.md` — harness reference and audit results.
- `AGENTS.md` § Testing expectations — the four banned patterns, restated as rules.
