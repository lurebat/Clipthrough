# AGENTS.md

## Repo guidance

- Keep changes focused and avoid unrelated cleanup.
- Prefer Avalonia APIs over Windows-specific interop unless platform APIs are required.
- Use `apply_patch` for manual edits and keep changes ASCII unless the file already requires Unicode.
- Preserve existing UX and data compatibility when changing clipboard storage or schema behavior.

## Validation

- Run `dotnet test .\Clipthrough.Tests\Clipthrough.Tests.csproj` for code changes.
- If app startup, packaging, or Avalonia views are touched, also run:
  - `dotnet build .\Clipthrough\Clipthrough.csproj`

## Testing expectations

- Add unit tests for pure formatting/parsing logic.
- Add SQLite-backed tests for persistence behavior.
- Add Avalonia headless tests for view/control loading and input where UI behavior matters.

## Docs

- Update `README.md` when setup, workflow, or user-visible capabilities change.
