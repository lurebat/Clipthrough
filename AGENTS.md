# AGENTS.md

Guidance for AI coding agents working on Clipthrough.

## What this repo is

Clipthrough is a cross-platform clipboard manager built with Avalonia 12 and
.NET 10. It captures clipboard events, stores them in SQLite, and surfaces them
through a rich history UI with sensitivity rules, text transformations, paste
shortcuts, and pluggable source attribution.

## Project structure

- `Clipthrough/` — application project
  - `Views/` — AXAML windows and controls (MainWindow, SettingsWindow, SessionLogsWindow, HelpWindow, AboutWindow)
  - `ViewModels/` — ReactiveUI-based VMs (MainWindowViewModel is the largest)
  - `Services/` — behavior abstractions organised into subfolders (`Ai/`, `Background/`, `Capture/`, `Imaging/`, `Ocr/`, `Platform/`, `Search/`, `Security/`, `Storage/`, `System/`). Every service has an `I<Name>Service` interface. All files keep the flat `Clipthrough.Services` namespace except `Platform/` (`Clipthrough.Services.Platform`, OS-specific concrete implementations) and `Search/` (`Clipthrough.Services.Search`).
  - `Models/` — POCOs for persisted state, settings, and transient records.
  - `Converters/` — `IValueConverter` implementations used in AXAML bindings.
  - `Styles/` — `ModernTheme.axaml` + class-based selectors.
  - `Localization/` — `AppText` static dictionary (single source of truth for user-visible strings).
  - `Data/` — SQLite helpers, schema migrations.
- `Clipthrough.Tests/` — xUnit test project
  - `TestDoubles.cs` — fakes/stubs for each service interface.
  - Service tests use real SQLite (in-memory or temp files).
  - Headless Avalonia tests live in `*HeadlessTests.cs` — a few still flake in cleanup; exclude them via `--filter "FullyQualifiedName!~HeadlessTests"` if you need a fast run.
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
- **AI transforms** (`IAiTransformService` / `AiTransformService`): OpenAI-compatible chat-completions client. Base URL + API key + model come from `AppSettings.Ai*` with env-var fallback (`OPENAI_BASE_URL`, `OPENAI_API_KEY`). Service has a test-friendly ctor taking an `HttpClient`.
- **Text transformations** (`TextTransformationService.Apply`): pure static function mapping `TextTransformation` → output. The `BoxTableToHtml` parser scans line-by-line, detects consecutive table-line blocks (box-drawing, Markdown `|---|`, or ASCII `+---+`), converts each to a `<table>`, and preserves surrounding text as escaped `<div>...<br>...</div>` blocks. Returns the original input verbatim if no table block is found.
- **Transform menus** (top `Edit > Transform`, toolbar flyout, right-click context menu): driven by `s_transformMenuEntries` in `MainWindow.axaml.cs` keyed by `(Group, Header, TextTransformation)`. `BuildTransformMenuItems` groups by `Group`; groups with a single entry are flattened to top-level. The right-click context menu in `MainWindow.axaml` lists the same transforms statically, because it applies them to the clip that was right-clicked rather than to the current selection — a different command on a different view model, so it cannot be generated from the table. Adding a transform means editing both, but you no longer have to remember to: `TransformMenuParityHeadlessTests` fails when the two lists differ in membership or in wording, and when a new `TextTransformation` reaches neither. Withholding one on purpose is allowed and has to be named in that test's `withheld` set, which is also checked for staleness.
- **Auto-copy after transform**: `ApplyTransformToTargetsAsync` and `ApplyTransformationToSingleClipAsync` invoke `CopyTransformResultToClipboardAsync` whenever exactly one target was transformed (single clip or selection slice). HTML-output transforms write CF_HTML via `CopyRichContentAsync`; everything else uses `CopyTextAsync`. Always call `_clipboardMonitorService.SuppressNext()` first so the capture doesn't double-create the clip. Multi-clip transforms intentionally do NOT auto-copy.
- **Custom hotkey bindings** (`Models/CustomHotkeyBinding.cs`, `Models/CustomHotkeyTarget.cs`, `App.axaml.cs#ExecuteCustomHotkey`): `Target` uses `kind:value` with **four** kinds — `builtin` (a `TextTransformation` member), `ai` (a saved preset name), `prompt` (an inline system prompt sent straight to `IAiTransformService.TransformAsync`, for one-offs with no preset), and `aiprompt` (opens the AI prompt dialog; needs no clip and pastes nothing). Parsing lives in `CustomHotkeyTarget.Parse` and is covered by `CustomHotkeyTargetTests`; only the **first** colon separates, because a prompt routinely contains one. The kind is lowercased and its token is preserved verbatim, since a transformed clip persists `TransformKind` as `token:value`. An unparseable target is silent by nature — the gesture is registered, the key is swallowed, nothing happens — so it is worth a test rather than a try.
- **Image AI transforms**: image clips can use AI presets/prompts in both image-to-text and image-to-image modes. Keep prompt UX and command routing aligned with the selected clip type.
- **Session logs** (`ISessionLogService` / `SessionLogService`): user-facing session logs are fed from `Trace`. Preserve real app warnings/errors, but avoid surfacing known benign framework noise that would drown out actionable entries.

## Validation

Before committing any non-doc change:

```
dotnet build .\Clipthrough\Clipthrough.csproj
dotnet test .\Clipthrough.Tests\Clipthrough.Tests.csproj --filter "FullyQualifiedName!~HeadlessTests"
```

The build is expected to be **0 warnings, 0 errors**, and a small set of
analyzer rules is enforced at `error` so a violation breaks the build rather
than joining a backlog nobody reads. The set and — just as importantly — the
rules deliberately *not* enforced and why are documented inline in
`.editorconfig`. Read that before adding a `#pragma warning disable`: several
rules were already evaluated and turned off on purpose, and two (CA1001,
CA2213) were enforced and then withdrawn because the analyzer's advice would
have reintroduced a fixed shutdown crash.

The enforced set is scoped to shipping code; `Clipthrough.Tests/**` exempts
the rules that only produce noise there (xUnit's own `Assert.Contains`
overloads, and `CA2201` where raising a reserved exception type *is* the
behaviour under test). `external/.editorconfig` is a `root = true` shield so
none of this fires on vendored code.

The headless filter is there because a few Avalonia headless tests still fail
intermittently — a couple of percent of runs — with a `[Test Case Cleanup
Failure]` naming an innocent test. That is work outliving a previous test, not
a bug in the test named; see
`docs/solutions/headless-teardown-leaks-dispatcher-jobs.md` before spending
time on it. Run them locally if you are specifically touching view code, and
re-run before believing a single red result.

If Clipthrough is running it holds a lock on its own output, and any build that
needs to copy a changed assembly into it fails with `MSB3027` / `MSB3021` on
`ShareX.ImageEditor.dll`. (A build with nothing to copy still succeeds, so the
failure looks intermittent.) Rather than killing an app the user may be using,
redirect the output — note the path must be **absolute**, because a relative
one is resolved per-project and scatters an output tree under every project in
the build:

```
dotnet test .\Clipthrough.Tests\Clipthrough.Tests.csproj -p:BaseOutputPath=C:\full\path\to\Clipthrough.Tests\artifacts\bin\
```

`Clipthrough.Tests/artifacts/` is gitignored. Do not also override
`BaseIntermediateOutputPath` — that invalidates the restore assets and fails
with `NETSDK1005`. When the lock does bite, MSBuild can leave the previous
assembly in place, so the tests silently run the old build; treat an
unexpectedly passing run after a lock error as untrustworthy.

## Testing expectations

- **Pure logic** (formatting, parsing, fuzzy matching, text transformation): unit tests in the service's test class.
- **Persistence** (schema, migrations, store behavior): SQLite-backed tests using an in-memory or temp-file connection factory.
- **Views / input**: Avalonia headless tests for control loading, bindings, and basic input. Keep these small — they're flaky under load. Tear the window and view model down *before* draining the dispatcher in any test harness; draining first leaves the jobs they post for Avalonia's post-test `RunJobs()`, which fails a later test in cleanup (`docs/solutions/headless-teardown-leaks-dispatcher-jobs.md`).
- **Fakes**: extend `TestDoubles.cs` when adding new service methods. Keep the fakes minimal but realistic.

### Prove a regression test can fail

A test that passes against the bug it was written to catch is worse than no
test: it reports safety that does not exist. Several in this repo did exactly
that, and each was caught only by luck or by a reviewer.

So for any test written to defend a specific fix, break the fix on purpose and
watch the test fail before you commit. `Clipthrough.Tests/Mutation/` automates
this — add the mutant to `mutants.json` and run
`pwsh Clipthrough.Tests\Mutation\Invoke-MutationCheck.ps1`. Note that coverage
cannot substitute: every vacuous test found here fully executed the code it
failed to defend.

A mutant's `find` anchor names a literal fragment of source, so it rots the
moment someone renames, rewords or renumbers that line - and a rotted mutant
still reads as coverage while testing nothing. Five had rotted before anyone
noticed, one of them within the same session that added it, because the full
sweep takes hours and is therefore rarely run. `-ValidateOnly` checks every
anchor without building anything and takes about two seconds:

```
pwsh Clipthrough.Tests\Mutation\Invoke-MutationCheck.ps1 -ValidateOnly
```

Run it after any refactor that touches a line a mutant points at, which in
practice means run it whenever you have changed shipping code.

Design the mutation so the mutated file still compiles: the analyzer set is
enforced at error, so a mutant that orphans a field or a method fails the build
and reports INCONCLUSIVE, which proves nothing. `if (false)` around the only use
of a private field trips CA1823 that way; keep the symbol referenced instead -
for example, waiting on `Task.CompletedTask` with the timeout the real call no
longer uses.

These four patterns produced the vacuous tests. Avoid them by construction:

- **Never let the old implementation be the oracle.** An equivalence test that
  compares new behaviour against the old code passes trivially once someone
  reverts to the old code. Pin the new behaviour to something independent — the
  index definition read back from `sqlite_master`, a fixture with hand-written
  expectations, a golden file.
- **Do not assert on a proxy without proving it discriminates.** `EXPLAIN QUERY
  PLAN` output for the `Alphabetical` sort is byte-identical with and without
  its optimisation, so a plan assertion passes against a full revert. Before
  asserting on any derived artifact, check it actually differs when the code is
  wrong.
- **Assert on the identified event, not on a count or a bare "something
  failed".** An async test that waited for *an* error passed because a
  completely different operation errored first. Match the specific traced
  context, and settle unrelated work before asserting.
- **Assert over intermediate states when the bug is transient.** If a later
  refresh repairs the state, a test that only inspects the end result sees
  nothing. Subscribe to `CollectionChanged` (or equivalent) and assert across
  the sequence.

- **Where two sources of truth should agree, write a test where they
  disagree.** When a value can be derived two ways - a CF_HTML fragment from
  the header offsets or from the `<!--StartFragment-->` comments, a length from
  a count or from a walk - the natural fixture is one where both give the same
  answer, so *every* such test passes whichever source the code consults. Ten
  tests over `ExtractHtmlFragment` had full line coverage and not one could tell
  the two orderings apart; the bug was which source was preferred. Build the
  fixture so the sources disagree, assert the right one wins, and assert the
  wrong one would have produced something else. That last assertion is what
  stops the fixture quietly becoming an agreeing one again. Confirmed against a
  second codebase: Vellum flipped the same ordering deliberately and its suite
  did fail - but through one fixture whose offsets happened to be wrong, so an
  author who computed honest offsets would have had the bug and the coverage.

- **Change one thing between the control and the subject.** Three separate
  wrong answers in one day came from a comparison where the difference that
  mattered was not the one under examination:

  - a regression test that showed a `Window`, so a four-minute run read as "the
    test is slow" rather than "the product hangs";
  - a baseline `TextLayout` measurement taken with `TextWrapping` left at its
    default - one line, no break search - which made a quadratic control look
    linear and produced a confident 29x attribution to the wrong layer;
  - the agreeing-fixtures case above.

  Before trusting a comparison, state what differs between the two sides and
  check it is exactly the thing being measured. When timing something, that
  usually means removing the rendering, the window, or the I/O rather than
  leaving them in as "realistic" - they are the confound, not the realism.

One measurement trap is worth knowing, because it makes manual verification lie:
restoring a file with `Copy-Item` or `Move-Item` restores its old timestamp, so
MSBuild thinks the assembly is current and silently runs the *previous* build.
Always `(Get-Item $path).LastWriteTime = Get-Date` after restoring a file.

A second one: never leave a scratch copy of a production `.cs` file anywhere
inside a project cone, including `Clipthrough.Tests/artifacts/`. The SDK compiles
it, it shadows the real type, and the only signal is a `CS0436` *warning* while
the whole test class quietly binds to the copy. `artifacts/**` is excluded from
compilation for this reason; put scratch files in the session folder instead.

## Commits and code changes

- Small, focused commits. One feature or fix per commit. Descriptive message with a body when the change is more than cosmetic.
- Do not commit unrelated refactors or cleanup with a feature — split them.
- Include the `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>` trailer when an agent authored the change.
- **Never** commit `obj/`, `bin/`, `artifacts/`, `testobj/`, or `.user` files. `.gitignore` covers most but double-check `git status` before committing.
- Do not introduce Unicode characters into existing ASCII files unless required (prefer `->` over `→` in comments; `…` is fine in user-visible strings that already use it).

## Platform considerations

- **Windows** is the primary target. Linux and macOS should build but are not feature-complete. Platform-specific code lives in `Services/Platform/SystemInteractionService.cs` under `[SupportedOSPlatform("windows")]` guards.
- **Async void** is allowed only for event handlers. Prefer `async Task` everywhere else.
- **P/Invoke**: struct layouts must match Win32 exactly. Prefer `System.Runtime.InteropServices.LibraryImport` on .NET 10 where possible — note this is aspirational, not descriptive: the codebase is currently 48 `DllImport` and 0 `LibraryImport`, so follow the surrounding style when editing an existing interop block rather than converting it piecemeal.

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
- `CONCEPTS.md`: glossary of project-specific terms (ClipEntry, WAL, lazy hydration, MetaSegments, etc.). Consult before using a term you haven't seen in code, and update when introducing a new project-specific concept.
- `docs/solutions/`: verified fix and pattern docs (regression-prevention knowledge base). Each doc has YAML frontmatter (`tags`, `version`, `severity`). Read relevant docs before touching persistence, threading, background workers, or search. Add a doc after landing a non-obvious bug fix. See `docs/solutions/README.md` for structure and frontmatter schema.
- `.github/copilot-cli-skills/clipthrough-transforms.md`: agent-facing reference for built-in transformations, the auto-copy contract, AI presets, and the custom-hotkey `Target` syntax (`builtin:` / `ai:` / `prompt:`). Update when adding transforms or new hotkey kinds.
- `.github/copilot-cli-skills/clipthrough-storage-schema.md`: agent-facing reference for the SQLite schema, FTS5 triggers, indexes, and the migration pattern. Update on every schema change.
- `.github/copilot-cli-skills/clipthrough-platform-windows.md`: agent-facing reference for Windows interop — clipboard formats (CF_HTML wrapping), foreground capture + paste sequencing, AttachThreadInput, SendInput, and global hotkeys via `Win32Properties.AddWndProcHookCallback`. Update when changing platform behavior.
