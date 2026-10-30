# Clipthrough

Clipthrough is an Avalonia desktop clipboard history app focused on rich clipboard capture, source-app metadata, image previews, search, local persistence, and programmable automation.

## Features

- Clipboard history for text, rich text, images, and file lists
- In-place rich-text editing plus an embedded image editor before copying as a new clip
- Source application name and window title capture
- FTS5 full-text search with fuzzy option, favorites, sensitivity tagging, and clip pinning
- **Regex search** runs on .NET's non-backtracking engine by default, so no pattern can hang the list. Patterns that need backreferences or lookarounds report so; turn on **Settings → Search → Use the backtracking regex engine** for them. That engine stops a search that runs too long and says "pattern too slow" instead of freezing.
- **Excluded applications** — list apps in Settings whose clipboard writes are never captured. Entries match a process name, executable file name, full path, or a `*`/`?` wildcard (e.g. `1Password`, `KeePass.exe`, `*bitwarden*`, `C:\Program Files\Acme\*`). The check runs before Clipthrough reads the clipboard, so an excluded app's payload never enters the process. Best effort: a clipboard change Windows reports no owner for is still captured — apps needing a hard guarantee should publish the standard `Clipboard Viewer Ignore` format, which Clipthrough always honors.
- **Clipboard-history opt-out** — content its source marks `CanIncludeInClipboardHistory = 0` is not captured. Chrome, Edge and Bitwarden publish that for copied passwords. On by default; **Settings → Excluded applications** turns it off.
- **Drag and drop is an import** — the exclusion and opt-out rules above apply to clipboard capture. Content you drop onto Clipthrough is a deliberate import and is kept even when its source app is excluded, publishes `Clipboard Viewer Ignore`, or marks it `CanIncludeInClipboardHistory = 0`.
- Per-session log window with a filterable activity trail
- Configurable local hotkeys (recorded in the settings window) and global hotkeys for paste cycling
- First-run welcome flow for storage path, password, and hotkey setup
- Fits any screen from a 576 DIP portrait monitor to an ultrawide: the layout reflows below 1100 DIP (the preview moves under the list on narrow screens, and short screens hide the footer). Every window stays inside its monitor's working area, including after a DPI change, a monitor change, or a show from the tray. The window reopens at the size, position and maximized state it was left in.
- SQLite database with optional encryption password, retention policies, and size caps
- Clip export with original payload, rendered text, and metadata (sensitive clips ask first; Open in editor and Compare use temporary working copies that are removed on exit)
- Top menu bar and an in-app Help window
- **AI transforms** — send text or image clips to any OpenAI-compatible chat-completions endpoint with a custom instruction or preset; results are saved as new clips (`Edit -> AI transform...`, `Ctrl+I`). Image presets support both image-to-text and image-to-image flows. Configure base URL, API key and model in Settings, or fall back to `OPENAI_BASE_URL` / `OPENAI_API_KEY` environment variables.
- **Text transformations** — `Edit -> Transform` (and the per-clip context menu) groups built-ins into **Case**, **Whitespace**, **Lines**, plus a **Text table → HTML** action that converts box-drawing, Markdown pipe, and ASCII `+---+` tables (handles multiple tables and surrounding text) into HTML you can paste into Teams/Outlook with cell formatting preserved. Single-clip transforms automatically place the result on the OS clipboard so you can paste immediately.
- **Text transforms** — case, whitespace, line, encoding, and table-to-HTML operations from `Edit → Transform`. Transforms respect the editor text selection — selecting part of a clip and running a transform rewrites only that range.
- **Custom hotkey actions** — bind any global hotkey to a one-shot transform of the most recent clip. Targets are `builtin:<TextTransformation>`, `ai:<PresetName>`, or `prompt:<free-form prompt>` for ad-hoc AI prompts without saving a preset.
- **OCR** — `Edit -> Extract text from image (OCR)` runs Windows.Media.Ocr on the selected image clip and captures the recognized text as a new clip. Optional background OCR can process new image clips automatically and reports status in the main window. Install additional Windows language packs (with the optional OCR feature) and list their BCP-47 tags in Settings (e.g. `en+he`).
- **Auto-update** — Velopack-based update channel using the GitHub Releases feed by default. Updates are downloaded in the background and applied when Clipthrough exits or on the next launch.
- **Release polish** — Help -> About shows the current app version, image clips can use a dedicated external editor path, and session logs capture app-level warnings without known benign Avalonia compositor noise.
- Windows publish artifact via GitHub Actions, plus a tagged-release workflow

## Where Clipthrough keeps its data

By default Clipthrough keeps its settings, logs and window position in `%LOCALAPPDATA%\Clipthrough`, and the clip library in `%LOCALAPPDATA%\ClipthroughData\clipthrough.db`. Three ways choose another data folder. The first one present wins:

1. **The `--data-dir <folder>` command-line option**, for example `Clipthrough.exe --data-dir "D:\Clipthrough data"`. `--data-dir=<folder>` works too.
2. **The `CLIPTHROUGH_DATA_DIR` environment variable**, set to a full path such as `D:\Clipthrough data` or `\\server\share\Clipthrough`. A relative value is refused, because the folder Clipthrough starts in differs between a shortcut, a script and sign-in. (A relative `--data-dir` is taken from the directory you start Clipthrough in.)
3. **Portable mode**: an empty file named exactly `portable.flag` beside Clipthrough keeps all data in a `ClipthroughData` folder next to it.
   - For the plain ZIP, put `portable.flag` beside `Clipthrough.exe`. The data then goes to `ClipthroughData` in that same folder.
   - For the Velopack `-Portable.zip`, put `portable.flag` in the top folder, beside `Update.exe` and the `Clipthrough.exe` launcher, not inside `current\`. Each update replaces `current\`, and it would delete a marker or data kept there. A marker inside `current\` is ignored. Velopack's own `.portable` file does not turn on portable mode.

With a chosen folder, everything lives inside it, the library included (`clipthrough.db`, unless you move it in Settings). In portable mode a library inside the portable folder is recorded relative to it, so it is still found after the folder moves to another drive letter or parent folder. After an update, Clipthrough restarts on the same data folder. It passes `--data-dir` to the restart when that option chose the folder.

If the chosen folder can't be used, Clipthrough shows a message and does not start. This happens when no folder follows `--data-dir` (a value starting with `-`, as in `--data-dir -p`, counts as no folder), when the variable is blank or relative, when the path is invalid, or when Clipthrough can't create or write to the folder. Clipthrough never falls back to the default folder, so your clips never end up somewhere you didn't choose.

**Start with Windows** starts Clipthrough on the same data folder. With the variable or the portable marker, the sign-in entry is just `Clipthrough.exe`, because both are still there at sign-in. When `--data-dir` chose the folder, the entry carries it: `"…\Clipthrough.exe" --data-dir "D:\Clipthrough data"`. There is one sign-in entry per Windows user, not one per data folder. Turning Start with Windows on in a session started with `--data-dir`, such as an isolated test folder, points sign-in at that folder. The next start of your usual session points sign-in back, if Start with Windows is on there. Each session applies its own Start with Windows setting when it starts.

Only one Clipthrough runs at a time, whatever its data folder. A second copy started with a different `--data-dir` exits while the first is running.

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

Tag the commit `vX.Y.Z` and push. The `release.yml` workflow publishes a framework-dependent build, zips it, creates the Velopack installer/feed assets, and publishes a GitHub Release. The published release updates the default feed at `https://github.com/lurebat/Clipthrough/releases/latest/download`.

```powershell
git tag v0.5.0
git push origin v0.5.0
```

### Build a local release package

Use the same flow as the GitHub release workflow:

```powershell
dotnet restore .\Clipthrough.slnx
dotnet publish .\Clipthrough\Clipthrough.csproj --configuration Release -p:Version=0.5.0 --output .\artifacts\publish
Compress-Archive -Path .\artifacts\publish\* -DestinationPath .\artifacts\Clipthrough-0.5.0-win-x64.zip
vpk pack --packId Clipthrough --packVersion 0.5.0 --packDir .\artifacts\publish --mainExe Clipthrough.exe --outputDir .\artifacts\velopack
```

## Project layout

- `Clipthrough/` - application code
- `Clipthrough.Tests/` - unit, integration, and Avalonia headless tests
- `.github/copilot-cli-skills/` - agent-facing references for transformation/hotkey wiring, SQLite storage schema, and Windows platform interop
- `.github/workflows/` - CI build and test automation

## License

Released under the MIT License. See `LICENSE`.
