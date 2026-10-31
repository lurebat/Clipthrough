---
tags: [ui, windows, popup, rendering]
version: unreleased (after v0.17.1)
severity: p1
status: active
---

# The popup reopened on a stale frame

## Problem

After a search, every summon of the popup showed the previous session first:
the old query in the box and its results in the list, then the cleared list,
then the list scrolled to the middle, then the newest clip - four states over
300-420 ms on a 60k library, on every run of the measurement. Nothing in the
code was slow; the first frame was simply old.

## Root cause

Two things combined:

- `Window.Hide()` stops the window rendering (Avalonia calls `StopRendering`),
  and on the next `Show()` DWM presents the window's **last** surface until the
  app draws a new one.
- The reset a fresh popup needs - clear the search, re-query, select the newest
  clip, scroll it into view - ran on show (`SetMainWindowVisible(true)`), after
  the window was already on screen.

So the user watched the reset happen. An open animation (fade or scale) made it
worse: the stale frame flashed at full contrast before the fade began.

## Solution

Dismiss into **standby** instead of hiding (`Views/MainWindow.Presentation.cs`),
the pattern PowerToys Command Palette uses:

1. Start the reset first: `MainWindowViewModel.EnterStandbyAsync()` clears the
   search and selects the newest clip, and the selection is scrolled into view.
2. `ShowInTaskbar = false` - before the cloak, because the cloak's re-show
   would otherwise bring the button back.
3. `TryCloakWindow(releaseForeground: true)`: `DWMWA_CLOAK`, then `SW_HIDE` (so
   Windows picks the next foreground window as for a real hide) and `SW_SHOWNA`
   (so the window is "visible" again and keeps rendering, still cloaked).
4. Owned windows (Settings, AI prompt, logs) are cloaked with it.

On summon, `TryLeaveStandbyAsync()` holds the cloak until the reset's re-query
has landed (at most 250 ms), refits to the current screens (`Opened` is not
raised), waits a frame if the caret moved the window onto a screen with another
scaling, then uncloaks. The first frame is already the right one: 58-152 ms to
the first DWM-presented frame on the 60k library, with no later states.

The view model keeps the list live in standby - a clip copied while dismissed
is already in place on the summon - but `HotkeyTargetClip` treats standby as
gone, so a global hotkey never acts on a selection nobody is looking at.

Minimize-to-tray still hides: restoring a minimized window first would
activate it. Where cloaking is unavailable (not Windows, or DWM refuses),
`Dismiss()` hides exactly as before.

## Prevention

- **`Window.IsVisible` no longer means "the user can see it."** Use
  `MainWindow.IsPresentedToUser`. Three checks had to change: the toggle's
  "is it up?", opening an owned window ("show the popup first"), and whether a
  notification goes to the OS instead of a toast.
- Dismiss through `MainWindow.Dismiss()`, never `Hide()`; summon through
  `TryLeaveStandbyAsync()` / `LeaveStandbyNow()`.
- `PopupStandbyHeadlessTests` pins the state at the moment of the uncloak (the
  first frame the user gets), the reset-before-cloak order, the taskbar button,
  owned windows, the hotkey target, and the fallback. Each has a mutant in
  `Clipthrough.Tests/Mutation/mutants.json`.
- What headless tests cannot see - Alt+Tab, the real foreground hand-back, a
  second monitor - needs a live check on Windows after changes here.