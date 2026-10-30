---
tags: [ui, windows, layout, multi-monitor, dpi]
version: v0.17.1
severity: p1
status: active
---

# Windows off-screen on small, scaled or changed monitors

## Problem

On screens with a small working area in DIP, parts of Clipthrough's windows
were off-screen and could not be reached. The DIP size is the pixel size
divided by the scaling, so a portrait 1440x2992 screen at 250% has only
576 DIP across, and a 1280x720 laptop at 150% has about 432 DIP of height.

- **Main window:** it held the workspace at a 1100x640 DIP minimum, so even
  maximized it ran past the screen. Search, the filters, the preview and
  every toast were unreachable. "Create library" and "Unlock" were below a
  short screen (audit F-49).
- **Dialogs:** Settings, Session logs, Help and Re-encrypt opened larger
  than the screen with their buttons outside it.
- **Hotkey show:** placing the window near the text caret put it hundreds
  of DIP past the right edge of a 250% screen.
- **Monitor changes:** a window shown from the tray after a monitor change,
  or dragged to a higher-DPI monitor, kept a size and position that no
  longer fit.

## Root cause

Four separate assumptions, each wrong on a scaled or changed monitor:

1. **Windows clamping the window is not enough.** Windows clamps an oversized
   window to the screen, but Avalonia's first layout still uses the requested
   DIP size. Controls aligned to the right or bottom are laid out where the
   screen cannot show them.
2. **Avalonia's `CenterOwner` only clamps a window that already fits.**
   See `Window.SetWindowStartupLocation`. A dialog larger than its screen is
   centred on the owner with both edges off the screen, and its caption can
   end up above the top edge, where it cannot be dragged.
3. **Sizes are DIP and positions are pixels.** `Window.Width` is DIP, while
   `Screen.WorkingArea`, `Window.Position` and the caret are pixels.
   `PositionWindowNearCaret` compared `window.Width` with the pixel bounds, so
   at 250% it treated a 1360 DIP (3400 px) window as 1360 px wide.
4. **A one-time fit misses later changes.** Opened is raised on every Show,
   but the fit was guarded to run once, and nothing refitted after a DPI or
   monitor change.

## Solution

- **Pure geometry:** `Views/WindowGeometry.cs` has `Fit`, `FitSize`,
  `PlaceNearCaret` and `Restore`. They take a `ScreenArea`, which is the
  working area in pixels plus its scaling. Each conversion uses the scaling
  of the screen the window is going to, not the one it is on now.
- **Fit triggers:** `Views/WindowFitter.cs` fits a window each time it opens,
  including every show from the tray. It also refits after `ScalingChanged`
  (only when the window no longer fits, so a drag between monitors is not
  fought) and after the screens change. Every Clipthrough window calls
  `WindowFitter.Attach(this)` in its constructor.
- **Responsive workspace:** the main window has a 480x360 minimum and reflows
  instead of overflowing. `WorkspaceBreakpoints.Classify` sets the bp-* classes
  for Wide, Medium, Narrow and Short. `MainWindow.Layout.cs` moves the parts
  between grid rows and columns, because Avalonia grid definitions cannot be
  set from styles. Wide, at 1100 DIP and above, is the original layout.
- **Remembered placement:** `IWindowPlacementStore` writes window.json beside
  settings.json, in the background; the main window's `Closed` waits for the
  last write (`Flush`), because the process exits right after. It is restored
  through `WindowGeometry.Restore`, so a saved
  rectangle on a monitor that is gone comes back on the primary screen,
  clamped to it.

## Prevention

- **New windows:** call `WindowFitter.Attach(this)` in the constructor. Give
  the window content that scrolls, so that whatever is fitted to a short screen
  keeps its buttons outside the scroller.
- **Units:** never compare a `Width` or `Height` with a `PixelRect`. Go through
  `WindowGeometry`, which states the unit of each parameter.
- **Layout tests:** test against the working area, not the window. A window
  held above the screen by its minimum lays out at that minimum, so "inside the
  client area" proves nothing. Use `Clipthrough.Tests/Layout/OnScreen.cs`.
- **Test screens:** headless has a single 1920x1280 stub screen. Install
  `TestScreens` with `WindowFitter.UseScreens` before creating a window, so the
  fit runs against a portrait or laptop screen.
- **Fonts in tests:** the test app loads Inter with `.WithInterFont()`, as the app does. Without it, headless measures text in a stub font about 1.6 times as wide, and every assertion about what fits measures text that does not exist.
- **Regression tests:** `ResponsiveWorkspaceHeadlessTests` covers each
  breakpoint; `WindowFitHeadlessTests` covers open, DPI change, screen change,
  show from the tray, caret placement and dialogs; `SettingsNarrowHeadlessTests`
  covers the hotkey rows; `WindowPlacementHeadlessTests` covers restoring the
  placement.
- **Screenshots:** `LayoutSurveyCapture` renders every surface at seven
  working areas when `CLIPTHROUGH_LAYOUT_CAPTURE_DIR` is set. Run it before and
  after any layout change.
