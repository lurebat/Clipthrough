# Clipthrough

Clipthrough is an Avalonia desktop clipboard history app focused on rich clipboard capture, source-app metadata, image previews, search, and local persistence.

## Features

- Clipboard history for text, rich text, images, and file lists
- In-place rich-text editing plus an embedded image editor before copying as a new clip
- Source application name and icon capture
- Search, favorites, sensitivity tagging, copy-count metadata, and per-session logs
- First-run welcome flow for storage path, password, and hotkey setup
- Configurable SQLite database path with optional encryption password
- Editable sensitivity rules plus optional retention and capacity limits
- Clip export with original payload, rendered text, and metadata
- Windows publish artifact via GitHub Actions

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

## Project layout

- `Clipthrough/` - application code
- `Clipthrough.Tests/` - unit, integration, and Avalonia headless tests
- `.github/workflows/` - CI build and test automation

## License

Released under the MIT License. See `LICENSE`.
