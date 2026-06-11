# refactor: Extract Settings + AI-transform from MainWindowViewModel (#10 continuation)

**Created:** 2026-06-10
**Type:** refactor
**Depth:** Deep
**Branch:** continues `fix/prod-hardening`
**Origin:** review finding #10 (god-object). Three clean seams already extracted (`UpdateViewModel`,
`DatabaseMaintenanceViewModel`, `CopilotViewModel`). This plan covers the two remaining clusters,
which are large and coupled enough to need their own planned, test-gated effort.

---

## Summary

`MainWindowViewModel` is still ~6,800 lines. The two biggest remaining clusters are **Settings**
(136 `Settings*` properties, 148 `{Binding Settings…}` bindings in `SettingsWindow.axaml`, plus the
save orchestration) and the **AI prompt/transform** (~25 members + ~15 methods, woven into the
clip-list). Both were deliberately deferred from the incremental seam pass because, unlike the three
clean seams, they have heavy coupling and a weak automated safety net (the headless suite has ~17
pre-existing flaky failures, so it cannot reliably catch a behavioral regression in the
settings/storage flow). This plan sequences them safely behind characterization tests.

---

## Problem Frame

- **Settings cluster:** the `SettingsWindow.axaml` form binds 148 `Settings*` members directly on
  `MainWindowViewModel`. The draft state (provider/key/url, OCR, remote-API, storage path/password,
  theme, script drafts, custom-hotkey drafts, sensitivity rules, section expand/visibility) is
  loaded by `LoadSettingsDraft`, synced by `OnSettingsChanged`, and applied by `SaveSettingsAsync` —
  which performs **storage-lifecycle work** (rekey, DB-path move, encryption toggle, worker restart)
  that was just hardened in Phase 2. The form *state* is separable; the *apply* is storage logic
  that must stay coordinated with `StorageOptionsService`.
- **AI prompt/transform cluster:** the prompt UI state (`IsAiPromptOpen`, `AiPromptInput`, …),
  preset/menu state (`AiPresets`, `AiMenuEntries`, `VisibleAiMenuEntries`, `IsAiMenuVisible`), and
  transform execution (`SubmitAiPromptAsync`, `RunAiPromptAsync`, `SubmitImageAiPromptAsync`,
  `QueueImageAiTransform`, `ApplyAiPresetAsync`, `RunImagePresetAsync`, `InvokeAiMenuEntryAsync`,
  `RefreshVisibleTransformMenus`) are interleaved with **shared clip-list accessors**
  (`GetCheckedOrSelectedClips`, `GetEffectiveSelectedClip`, `GetTransformSourceText`) that OCR and
  text-transform also use, and with clip-presentation props (`Has*/Can*Transform`). It is not a
  standalone seam — it is clip-list logic with an AI flavor.

---

## Requirements

- **R1 — Behavior preservation.** Every extraction is behavior-identical; no settings/storage/AI
  behavior changes. Verified by tests, not just compile.
- **R2 — Characterization safety net first.** Because the headless suite is flaky, add focused,
  *reliable* (non-headless where possible) tests for the settings save/load round-trip and the AI
  transform dispatch BEFORE extracting, so a regression is caught.
- **R3 — Settings form state lives in `SettingsViewModel`;** storage-apply stays coordinated with
  `StorageOptionsService` (do not fragment the Phase 2 rekey/move/restore flow).
- **R4 — AI prompt/transform** moves behind a small clip-selection abstraction rather than a raw
  back-reference, so the shared accessors are not duplicated.
- **R5 — Each unit is an independently shippable, verified commit** (the proven seam pattern).

---

## Key Technical Decisions

- **KTD1 — `SettingsViewModel` owns draft state; `MainWindowViewModel` owns apply.** Move the 136
  `Settings*` properties + the script/hotkey/sensitivity draft collections into `SettingsViewModel`,
  exposed as `MainWindowViewModel.Settings`. `LoadSettingsDraft`/`OnSettingsChanged` populate it;
  `SaveSettingsAsync` (staying in MWVM, because it drives `StorageOptionsService` rekey/move/restart)
  reads the draft off `Settings`. **Do NOT** move the storage-apply into the sub-VM.
- **KTD2 — Repoint bindings via the property prefix, not a DataContext swap.** Keep
  `SettingsWindow`'s DataContext as `MainWindowViewModel` and change the 148 bindings from
  `{Binding SettingsX}` to `{Binding Settings.X}`. A DataContext swap would also require moving the
  non-settings bindings on that window (status, dialogs) and is riskier. Mechanical, scriptable
  rename; verify each section compiles (Avalonia compiled bindings validate the path).
- **KTD3 — Extract `SettingsViewModel` in section sub-units, not one mega-edit.** The 136 props
  group into sections (AI, OCR, remote-API, storage, update, theme, scripts, hotkeys, sensitivity,
  section-expansion). Move + rebind one section per commit so each diff is reviewable and each is
  independently verified.
- **KTD4 — AI: introduce `IClipSelectionSource`** (`GetCheckedOrSelectedClips()`,
  `GetEffectiveSelectedClip()`, `GetTransformSourceText()`) implemented by `MainWindowViewModel`.
  `AiTransformViewModel` takes that interface + the AI services + a status callback + the clip store,
  holds the prompt/preset/menu state and the transform methods, and is exposed as `MWVM.Ai`. MWVM
  calls `Ai.RefreshVisibleTransformMenus()` from its selection-change handlers. Keep
  `_aiTransformService` shared (MWVM's `Has*/Can*Transform` props still read `.IsConfigured`).
- **KTD5 — Migrate the 12 code-behind refs** (`IsAiPromptOpen` ×11 in `MainWindow.axaml.cs`,
  `AiPromptInput` ×1 in `App.axaml.cs`) to `Ai.IsPromptOpen` / `Ai.PromptInput` as part of the AI unit.

---

## Implementation Units

### U1. Characterization tests (do first — R2)
- **Goal:** a reliable safety net the flaky headless suite doesn't provide.
- **Files:** `Clipthrough.Tests/Unit/` (new `SettingsDraftRoundTripTests.cs`), extend AI dispatch tests.
- **Approach:** test `LoadSettingsDraft` → mutate draft → `SaveSettingsAsync` round-trips to
  `AppSettings`/`StorageOptions` (mock storage where the apply touches it); test AI transform dispatch
  routes text vs image to the right path given a fake clip-selection source. Prefer non-headless.
- **Verification:** these tests pass on the current code and will guard every subsequent unit.

### U2–U6. Settings sections (one per commit)
- **Goal:** move each settings section's props + drafts into `SettingsViewModel`, repoint that
  section's bindings to `Settings.*`.
- **Suggested split:** U2 AI-settings (provider/key/url/model/reasoning/image-model) · U3 OCR +
  remote-API · U4 storage (path/password/encryption/diff-tool) · U5 update + theme + misc scalars ·
  U6 script/custom-hotkey/sensitivity draft collections + their add/remove commands.
- **Files:** new `Clipthrough/ViewModels/SettingsViewModel.cs` (created in U2, grown per unit),
  `MainWindowViewModel.cs`, `Clipthrough/Views/SettingsWindow.axaml`.
- **Approach:** per KTD1/KTD2/KTD3. `SaveSettingsAsync`/`LoadSettingsDraft`/`OnSettingsChanged` read
  and write `Settings.*` instead of local fields; the section-visibility/expansion props move too.
- **Verification:** build (compiled-binding path check) + U1 tests + headless within baseline, per unit.

### U7. `AiTransformViewModel` + `IClipSelectionSource`
- **Goal:** move the AI prompt/transform/preset/menu cluster out behind a clip-selection abstraction.
- **Files:** new `Clipthrough/Services/IClipSelectionSource.cs` (or a VM-local interface), new
  `Clipthrough/ViewModels/AiTransformViewModel.cs`, `MainWindowViewModel.cs`,
  `Clipthrough/Views/AiPromptWindow.axaml`, `Clipthrough/Views/MainWindow.axaml` (AI submenu),
  `Clipthrough/Views/MainWindow.axaml.cs`, `Clipthrough/App.axaml.cs`.
- **Approach:** per KTD4/KTD5. MWVM implements `IClipSelectionSource`; keeps `_aiTransformService`
  shared and `Has*/Can*Transform`/`RefreshVisibleTransformMenus` wiring intact (calling `Ai.…`).
- **Verification:** build + U1 AI-dispatch tests + headless baseline + manual smoke of an AI transform.

---

## Risks & Mitigations

- **148 bindings + compiled bindings:** Avalonia validates `Settings.X` paths at build, so a wrong
  path fails the build (good). Risk is a *missed* binding still pointing at the moved member → build
  error, also caught. Net: build is a strong gate for the rename. Do it per-section (KTD3).
- **Save-orchestration coupling (storage/rekey/workers):** keep `SaveSettingsAsync` in MWVM (KTD1);
  do not move the Phase 2 lifecycle code. U1 round-trip test guards it.
- **Flaky headless net:** U1 characterization tests are the real gate; treat headless count drift
  within 16–19 as noise (established baseline), but investigate any *new* assertion-failing test in
  isolation (the established method).
- **AI clip-coupling:** `IClipSelectionSource` keeps the shared accessors single-sourced in MWVM;
  do not duplicate them into the sub-VM.

---

## Verification Strategy

Per unit: build clean, U1 characterization tests green, full non-headless suite green, headless
within the 16–19 flaky baseline (new in-isolation failures investigated). Add the AI-transform
dispatch test and the settings round-trip test as permanent coverage. Manual: open Settings, change
each section, save, reopen; run an AI text + image transform; Copilot sign-in (already extracted).

---

## Out of scope

- Behavior changes of any kind. - Touching the Phase 2 storage-lifecycle code. - The clip-list
  itself (only the `IClipSelectionSource` seam is introduced; a full clip-list controller extraction
  is a separate future effort).
