# docs/solutions/

Knowledge store for verified fixes, design patterns, and non-obvious decisions
encountered during Clipthrough development. Docs here are written *after* the
code ships and the fix is confirmed — never speculatively.

## When to use this store

- Before implementing a feature or fix in an area already documented here.
- When a bug recurs and you want to understand whether a prior solution applies.
- When writing or reviewing code that touches persistence, threading, background
  workers, or search — the most regression-prone subsystems.

## When to add a doc

Add a doc when:
1. A bug was non-obvious to root-cause and the fix has a specific invariant that
   must not regress.
2. A pattern is used in multiple places and deviating from it causes breakage.
3. A decision has a strong "why not the obvious alternative" that future agents
   will otherwise question.

Do not add docs for decisions fully covered by `AGENTS.md` or by inline code
comments alone (unless the comment is too local to show up during exploration).

## Naming

`kebab-case-description.md` — describe the problem or pattern, not the solution.
Example: `metadata-only-list-reads.md`, not `how-we-fixed-oom.md`.

## Frontmatter schema

Every solution doc starts with a YAML front-matter block:

```yaml
---
tags: [<subsystem>, ...]          # e.g. sqlite, threading, search, workers, capture
version: <vX.Y.Z>                 # release that shipped or verified the fix
severity: p0 | p1 | p2           # regression risk if violated
status: active                    # active | superseded
superseded_by: <filename>         # only when status: superseded
---
```

Required fields: `tags`, `version`, `severity`, `status`.

## Sections

Each doc should have these four sections (add others only if clearly useful):

- **Problem** — what went wrong or what invariant is easy to miss.
- **Root cause** — why it happened; what assumption was wrong.
- **Solution** — the code pattern or approach used to fix it.
- **Prevention** — how future changes can avoid re-introducing the bug; what
  tests or code patterns guard this invariant.
