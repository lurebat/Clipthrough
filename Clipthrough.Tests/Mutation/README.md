# Mutation checks

These files check the *tests*, not the code.

A test that passes against the bug it was written to catch is worse than no
test, because it reports safety that does not exist. Three tests written during
one review session did exactly that, and each was caught only by chance or by a
reviewer. This harness makes the check mechanical.

For each mutant in `mutants.json` the runner breaks the guarded code on purpose,
rebuilds, runs only the covering test, and requires that test to **fail**.

```pwsh
pwsh Clipthrough.Tests\Mutation\Invoke-MutationCheck.ps1
pwsh Clipthrough.Tests\Mutation\Invoke-MutationCheck.ps1 -Id deferred-refresh-rethrows
```

Roughly a minute per mutant, since each one is a fresh build plus a filtered
test run. Run it when you touch guarded code, not on every commit.

## Outcomes

| Outcome | Meaning |
| --- | --- |
| `KILLED` | The test failed as it should. The guard is real. |
| `SURVIVED` | The test passed against broken code. **The guard is fake.** |
| `INCONCLUSIVE` | The build broke, or the filter matched no tests. Proves nothing. |
| `STALE` | The anchor text no longer exists. The mutant needs updating. |

`INCONCLUSIVE` is reported separately on purpose. A compile error also makes
`dotnet test` exit non-zero, so treating "exit code was non-zero" as a kill is
how you end up trusting a test that never ran.

`STALE` is a hard failure rather than a skip. A mutant whose anchor no longer
matches silently tests nothing — which recreates the very false-confidence
problem this harness exists to prevent, one level up.

## Adding a mutant

```jsonc
{
  "id": "short-kebab-id",
  "file": "Clipthrough/Services/Storage/ClipStoreService.cs",  // repo-relative
  "why": "What breaks in production if this guard stops working.",
  "find": "exact source text, must occur exactly once",
  "replace": "the broken version",
  "filter": "TheTestThatMustCatchIt"
}
```

Pick the mutant that represents the *real* regression — usually a revert to the
previous implementation, or a plausible drift such as one of two coupled
constants changing alone. Then confirm it is `KILLED`. If it survives, the test
is the thing that needs fixing.

Point `filter` at the test that actually asserts the property. During this
harness's first run, `alphabetical-sort-index-dropped` survived because it named
a test that deliberately excludes Alphabetical; the fix was a missing assertion
(`Alphabetical_IsServedFromItsOwnIndex`), not a weaker mutant.

## Safety

Sources are backed up as raw bytes and restored in a `finally`, so a file with a
BOM or unusual line endings comes back byte-identical. If the run is killed
mid-mutation, `Clipthrough.Tests/artifacts/mutation-pending.json` survives and
the next run restores from it before doing anything else — production code can
never be left broken on disk.

After any run, `git diff -- Clipthrough/` should be empty.

## Why not Stryker.NET

Stryker is the standard .NET mutation tester and would automate mutant
*generation* too, but it does not work reliably with xUnit v3, which this
project uses: mutations fail to activate under the Microsoft Testing Platform
and mutants are reported as surviving wholesale. The documented workaround is
downgrading to xUnit v2, which is not worth doing to the test suite. Revisit if
that support lands.

Note also that coverage does not substitute for this. `coverlet.collector` is
already referenced, and all three vacuous tests had full coverage of the code
they failed to defend. Coverage measures execution, not discrimination.

## What the first audit found

Seven mutants representing plausible regressions were run against the whole
non-headless suite, to answer "does *any* existing test catch this?".

| Mutant | Outcome |
| --- | --- |
| Retention age cutoff inverted (control) | killed by 56 tests |
| Embedding drops the `is_sensitive` gate | killed by 1 |
| Embedding drops the sensitivity-scan gate | killed by 2 |
| Age retention deletes pinned/favorited clips | killed by 1 |
| Entry-count cap deletes pinned/favorited clips | killed by 1 |
| Library-size cap deletes pinned/favorited clips | killed by 1 |
| Sensitivity rules ignore the `IsEnabled` flag | **survived all 278** |

So the existing suite is largely sound, and the surviving gap was a service with
no direct tests at all rather than a weak assertion in an existing one. Writing
those tests then surfaced two real defects in the same file — an uncompilable
pattern being persisted before it was ever compiled, and unbounded regex
backtracking on the capture path.

The lesson worth repeating: mutants are cheapest to aim at code that has *no*
test file, not at code whose tests merely look thin.
