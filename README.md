# Clipthrough

Clipthrough is an Avalonia desktop clipboard history app focused on rich clipboard capture, source-app metadata, image previews, search, and local persistence.

## Features

- Clipboard history for text, rich text, images, and file lists
- In-place rich-text editing plus an embedded image editor before copying as a new clip
- Source application name and window title capture
- FTS5 full-text search with fuzzy option, favorites, sensitivity tagging, and clip pinning
- Per-session log window with a filterable activity trail
- Configurable local hotkeys (recorded in the settings window) and global hotkeys for paste cycling
- First-run welcome flow for storage path, password, and hotkey setup
- SQLite database with optional encryption password, retention policies, and size caps
- Clip export with original payload, rendered text, and metadata
- Top menu bar and an in-app Help window
- Windows publish artifact via GitHub Actions, plus a tagged-release workflow

## Development

### Requirements

- .NET 10 SDK
- Windows for full clipboard and global hotkey behavior

### Run locally

```powershell
dotnet run --project .\Clipthrough\Clipthrough.csproj
```

### Run tests

```powershell
dotnet test .\Clipthrough.Tests\Clipthrough.Tests.csproj
```

### Release a new version

Tag the commit `vX.Y.Z` and push. The `release.yml` workflow publishes a framework-dependent build, zips it, and drafts a GitHub Release with the artifact attached. Review and publish the draft to promote the release.

```powershell
git tag v0.1.0
git push origin v0.1.0
```

## Project layout

- `Clipthrough/` - application code
- `Clipthrough.Tests/` - unit, integration, and Avalonia headless tests
- `.github/workflows/` - CI build and test automation

## License

Released under the MIT License. See `LICENSE`.
