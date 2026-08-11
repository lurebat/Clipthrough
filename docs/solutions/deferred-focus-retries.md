---
tags: [ui, focus, dispatcher, avalonia, keyboard]
version: v0.13.0
severity: p2
status: active
---

# Deferred focus retries fight each other

## Problem

Focus jumped back to the search box a frame after the user had moved somewhere
else — into the clip list with Down or Tab, or while typing to filter. The
symptom was intermittent and priority-dependent, which made it look like an
Avalonia bug rather than the application's own code.

## Root cause

`FocusSearchBox` does not focus once. Avalonia + Win32 hand focus to the menu
bar when the window is activated, so the method scheduled the same focus call at
three dispatcher priorities (immediate, `Input`, `Background`) to win regardless
of when the menu's auto-focus ran.

Those retries had a single condition: "focus the search box unless the search
box already has focus". They had no way to tell *why* the search box had lost
focus. Anything that legitimately took focus after the first attempt but before
the `Background` retry — `FocusSelectedClipInList` (itself deferred to `Input`),
type-to-filter, a flyout — was undone by the trailing retry.

Because the two focus helpers post at different priorities, which one won
depended on how busy the dispatcher was.

## Solution

Two guards, both in `MainWindow.axaml.cs`:

1. **A request stamp.** `BeginFocusRequest()` increments
   `m_focusRequestGeneration`, and every scheduled focus job captures the value
   it was issued with. A job whose generation no longer matches is superseded
   and returns without touching focus. The most recent request wins.
   `FocusSearchBox`, `FocusSelectedClipInList` and the type-to-filter redirect
   all stamp.

2. **Retries stand down.** Only the *first*, explicit attempt focuses
   unconditionally; that is the caller's stated intent (Ctrl+F, Shift+Tab, Up
   from the first row) and must always work. The retries additionally require
   `IsFocusReclaimable()` — true only when nothing holds keyboard focus or the
   top menu does, which are the two states the retries exist to fix.

## Prevention

- Any new code that focuses a control should call `BeginFocusRequest()` first,
  otherwise a retry queued a moment earlier can still steal focus from it.
- Do not add the reclaimability check to the first attempt. Doing so breaks
  every explicit "focus the search box" path from the clip list, because the
  list legitimately holds focus at that moment.
- Regression test: `ASearchBoxFocusRetry_DoesNotOverrideALaterMoveToTheClipList`
  issues both focus requests back to back and drains the dispatcher, which
  reproduces the race deterministically. Verified to fail on the pre-fix code.

## Testing note

The `Background`-priority retry is not reachable through
`HeadlessWindowExtensions.KeyPress` — the key press appears to drain the queue
in a way that runs the retry before the handler's own `Input` job. A test built
on `KeyPress` therefore passes with or without the fix. Drive the two focus
helpers directly instead (`FocusSelectedClipForTests` exists as the seam) and
confirm any new focus test fails when the guard is removed.
