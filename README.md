# Clipthrough

Clipthrough is an Avalonia desktop clipboard history app focused on rich clipboard capture, source-app metadata, image previews, search, local persistence, and programmable automation.

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
- **AI transforms** — send text or image clips to any OpenAI-compatible chat-completions endpoint with a custom instruction or preset; results are saved as new clips (`Edit -> AI transform...`, `Ctrl+I`). Image presets support both image-to-text and image-to-image flows. Configure base URL, API key and model in Settings, or fall back to `OPENAI_BASE_URL` / `OPENAI_API_KEY` environment variables.
- **User scripting** — write C# scripts that transform clip text. Scripts live in settings; `Edit → Run script` lists them. `Edit → Load default scripts` seeds JSON quote/unquote/minify/pretty, URL encode/decode, Base64 encode/decode, and a couple of whitespace helpers. Transforms respect the editor text selection — selecting part of a clip and running a transform rewrites only that range.
- **OCR** — `Edit -> Extract text from image (OCR)` runs Windows.Media.Ocr on the selected image clip and captures the recognized text as a new clip. Optional background OCR can process new image clips automatically and reports status in the main window. Install additional Windows language packs (with the optional OCR feature) and list their BCP-47 tags in Settings (e.g. `en+he`).
- **Auto-update** — optional Velopack-based update channel. Enable `Auto-update` in Settings and set the feed URL; updates are staged to apply on next launch. Releases are published via the tagged `release.yml` workflow which also invokes `vpk pack`.
- **Remote control API** — optional authenticated HTTP API for local tools and AI agents. It binds to loopback by default but can be reconfigured in Settings -> Remote API. Enable it and mint a bearer token in Settings -> Remote API. See `.github/copilot-cli-skills/clipthrough-remote-api.md` for the endpoint reference.
- **Release polish** — Help -> About shows the current app version, image clips can use a dedicated external editor path, and session logs capture app-level warnings without known benign Avalonia compositor noise.
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
dotnet test .\Clipthrough.Tests\Clipthrough.Tests.csproj --filter "FullyQualifiedName!~HeadlessTests"
```

Run the full test suite, including headless UI coverage, when you are specifically changing views or input behavior.

```powershell
dotnet test .\Clipthrough.Tests\Clipthrough.Tests.csproj
```

### Release a new version

Tag the commit `vX.Y.Z` and push. The `release.yml` workflow publishes a framework-dependent build, zips it, and drafts a GitHub Release with the artifact attached. Review and publish the draft to promote the release.

```powershell
git tag v0.1.0
git push origin v0.1.0
```

### Build a local release package

Use the same flow as the GitHub release workflow:

```powershell
dotnet restore .\Clipthrough.slnx
dotnet publish .\Clipthrough\Clipthrough.csproj --configuration Release -p:Version=0.1.0 --output .\artifacts\publish
Compress-Archive -Path .\artifacts\publish\* -DestinationPath .\artifacts\Clipthrough-0.1.0-win-x64.zip
vpk pack --packId Clipthrough --packVersion 0.1.0 --packDir .\artifacts\publish --mainExe Clipthrough.exe --outputDir .\artifacts\velopack
```

## Project layout

- `Clipthrough/` - application code
- `Clipthrough.Tests/` - unit, integration, and Avalonia headless tests
- `.github/copilot-cli-skills/` - agent-facing local API guidance
- `.github/workflows/` - CI build and test automation

## License

Released under the MIT License. See `LICENSE`.
