# Vendored: Vellum

Native Avalonia rich text editor. Replaces the WebView-based rich text viewer
(`RichWebContentView`) and, for plain text, `SyntaxTextEditor`. See
`docs/` in the upstream tree for the architecture and user guide.

## Provenance

| | |
|---|---|
| Upstream | `G:\fun\Vellum` (local-only; no git remote yet) |
| Branch | `user/asafmahlev/increment-0-spike` |
| Commit | `d93b1c241a00d4d02ee6aba155a73c4094ec84da` |
| Vendored | 2026-08-16 |
| Licence | MIT (see `LICENSE`) |

## Why vendored rather than a submodule or a package

- **NuGet is blocked on a name.** `Vellum.Core` is taken by an unrelated
  published library, and the rename is deliberately deferred upstream, so
  waiting for packages means waiting indefinitely. Only `PackageId` changes at
  rename time - assembly names, namespaces and `avares://` URIs are stable - so
  vendoring now costs nothing later.
- **A submodule needs a remote**, and Vellum has none.
- Clipthrough already vendors committed source this way for
  `external/ShareX.ImageEditor` and `external/ShareX`.

Prefer moving to a submodule the moment Vellum is pushed anywhere.

## What was copied

`src/` (the five shipping projects), plus `Directory.Build.props`, `README.md`
and `LICENSE` from the repository root. Tests, samples and the spike are not
vendored - they are run upstream.

**`Directory.Build.props` is not optional.** MSBuild walks *up* for it and
stops at the first hit, so without a copy at `external/Vellum/` these projects
would find Clipthrough's and build with `net10.0-windows` instead of
`net10.0`, without `TreatWarningsAsErrors` and without
`GenerateDocumentationFile` - compiling perfectly well, under rules Vellum is
never tested under. If a build starts emitting Vellum warnings, check that this
file still exists before anything else.

## Re-syncing

```powershell
$src = "G:\fun\Vellum"          # or the active worktree
$dst = "external\Vellum"
robocopy "$src\src" "$dst\src" /E /XD bin obj
Copy-Item "$src\Directory.Build.props","$src\README.md","$src\LICENSE" $dst
```

Then update the commit above, and run Clipthrough's suite. Do not edit anything
under this directory - fix it upstream and re-sync, or the next sync silently
reverts the fix.
