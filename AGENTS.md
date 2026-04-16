# AGENTS.md

Guidance for AI coding agents working on Clipthrough.

## What this repo is

Clipthrough is a cross-platform clipboard manager built with Avalonia 11.3 and
.NET 10. It captures clipboard events, stores them in SQLite, and surfaces them
through a rich history UI with sensitivity rules, text transformations, paste
shortcuts, and pluggable source attribution.

## Project structure

- `Clipthrough/` — application project
  - `Views/` — AXAML windows and controls (MainWindow, SettingsWindow, SessionLogsWindow, HelpWindow)
  - `ViewModels/` — ReactiveUI-based VMs (MainWindowViewModel is the largest)
  - `Services/` — behavior abstractions (clip store, sensitivity, settings, system interaction, clipboard monitor). Every service has an `I<Name>Service` interface.
  - `Models/` — POCOs for persisted state, settings, and transient records.
  - `Converters/` — `IValueConverter` implementations used in AXAML bindings.
  - `Styles/` — `ModernTheme.axaml` + class-based selectors.
  - `Localization/` — `AppText` static dictionary (single source of truth for user-visible strings).
  - `Data/` — SQLite helpers, schema migrations.
- `Clipthrough.Tests/` — xUnit test project
  - `TestDoubles.cs` — fakes/stubs for each service interface.
  - Service tests use real SQLite (in-memory or temp files).
  - Headless Avalonia tests live in `*HeadlessTests.cs` — some of these hang in CI; exclude them via `--filter "FullyQualifiedName!~HeadlessTests"` if you need a fast run.
- `external/` — vendored dependencies.
- `__decompiled/` — reference decompilation artifacts (do not edit, do not commit new ones).

## Architecture conventions

- **DI**: services are registered in `App.axaml.cs` → `ConfigureServices()`. Add new services there.
- **MVVM**: all UI state is on ViewModels. Views have minimal code-behind (only for window lifecycle, DataContext subscriptions, or interop that can't be bound).
- **ReactiveUI**: commands are `ReactiveCommand` with explicit signatures (`ReactiveCommand<TParam, TResult>`). Async commands use `CreateFromTask`. Never use `ICommand` directly.
- **Settings**: `IAppSettingsService` loads/saves the `AppSettings` record. The ViewModel exposes a mirror of each field with a `Settings*` prefix so editing can be transactional (Cancel reverts).
- **Localization**: user-visible strings come from `AppText`. Add new entries to the dictionary in `AppText.cs`. Never hardcode strings in AXAML or VMs.
- **Theming**: use class selectors (`Classes="surface-panel"`) from `ModernTheme.axaml`. Brushes come from `DynamicResource`s so they respect dark/light mode.
- **Persistence**: schema changes go in `Data/ClipSchema.cs` with an idempotent migration (ADD COLUMN IF NOT EXISTS, etc.). Bump version only when required.
- **Hotkeys**: global hotkeys are registered via `ISystemInteractionService.TryRegisterGlobalHotKey` which on Windows uses `RegisterHotKey` + `Win32Properties.AddWndProcHookCallback`. Never hold a managed `WndProc` delegate yourself — use the Avalonia helper to avoid GC crashes.

## Validation

Before committing any non-doc change:

```
dotnet build .\Clipthrough\Clipthrough.csproj
dotnet test .\Clipthrough.Tests\Clipthrough.Tests.csproj --filter "FullyQualifiedName!~HeadlessTests"
```

The headless filter is there because some Avalonia headless tests hang intermittently in CI. Run them locally if you are specifically touching view code.

## Testing expectations

- **Pure logic** (formatting, parsing, fuzzy matching, text transformation): unit tests in the service's test class.
- **Persistence** (schema, migrations, store behavior): SQLite-backed tests using an in-memory or temp-file connection factory.
- **Views / input**: Avalonia headless tests for control loading, bindings, and basic input. Keep these small — they're flaky under load.
- **Fakes**: extend `TestDoubles.cs` when adding new service methods. Keep the fakes minimal but realistic.

## Commits and code changes

- Small, focused commits. One feature or fix per commit. Descriptive message with a body when the change is more than cosmetic.
- Do not commit unrelated refactors or cleanup with a feature — split them.
- Include the `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>` trailer when an agent authored the change.
- **Never** commit `obj/`, `bin/`, `artifacts/`, `testobj/`, or `.user` files. `.gitignore` covers most but double-check `git status` before committing.
- Do not introduce Unicode characters into existing ASCII files unless required (prefer `->` over `→` in comments; `…` is fine in user-visible strings that already use it).

## Platform considerations

- **Windows** is the primary target. Linux and macOS should build but are not feature-complete. Platform-specific code lives in `Services/SystemInteractionService.cs` under `[SupportedOSPlatform("windows")]` guards.
- **Async void** is allowed only for event handlers. Prefer `async Task` everywhere else.
- **P/Invoke**: struct layouts must match Win32 exactly. Prefer `System.Runtime.InteropServices.LibraryImport` on .NET 10 where possible.

## How to add a feature (template)

1. Read the relevant checkpoint files in the session folder for prior context.
2. Sketch the model: what state changes, what new fields on `AppSettings` / clips.
3. If persistence changes, add a migration + test.
4. Add the service method (interface + impl). Extend `TestDoubles.cs`.
5. Wire command + UI state on the ViewModel.
6. Add AXAML bindings.
7. Update `AppText` if user-visible.
8. Tests: service-level + ideally a VM test.
9. Update README if user-visible.
10. `dotnet build` + `dotnet test`. Commit.

## Docs

- `README.md`: user-facing. Keep setup, feature list, and screenshots current when behavior changes.
- `AGENTS.md` (this file): developer/agent guidance. Update when architecture or conventions change.
- `CONTRIBUTING.md`: PR and style guidance.
