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
  fought), after the screens change, and when a drag by the caption ends on
  another screen (`WindowFitter.MoveSizeLoop`, from WM_EXITSIZEMOVE; again only
  when the window no longer fits, and never during the drag or after a
  resize). Every Clipthrough window calls
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

### Replacing Grid definitions at runtime crashed the app

The first version assigned new `RowDefinitions`/`ColumnDefinitions` straight
from `OnPropertyChanged(ClientSize)`. Changing the display mode between
1280x720 at 150% and the native mode then froze Clipthrough and killed it with
`ArgumentOutOfRangeException` in `Grid.MeasureCellsGroup`, thrown from
`Window.HandleResized` inside the window procedure.

Root cause, from the Avalonia 12.1.1 source: Grid caches each child's cell
(row and column, clamped to the definition counts of the time) and rebuilds
that cache only when a child's `Grid.Row`/`Column`/`Span` changes, the
children change, a spacing changes, or an existing definition changes unit
type. The definitions setters only call `InvalidateMeasure()`. The filter row
goes from three columns and one row (Short) to two columns and two rows (not
Short) with no child changing cell, so the Search options button stayed
cached at column 2 against two columns. A display-mode change crossed the
600 DIP Short line; resizing by hand rarely does so without also crossing a
width breakpoint, which moves children and clears the cache. Timing and
re-entrancy were not the cause: the same exception reproduces headless from
a single resize.

Fix: every definition change goes through `GridDefinitions.Set`, which assigns
the collections only when they differ and then clears the cell cache by
toggling the first definition's unit type (the one public route to the Grid's
internal `Invalidate()`). `RequestWorkspaceBreakpoint` also coalesces nested
requests. Regression tests: `WorkspaceBreakpointTransitionHeadlessTests`,
which record dispatcher exceptions, because the app's handler marks them
handled in headless and they show only as a window that kept its old size.
## Prevention

- **New windows:** call `WindowFitter.Attach(this)` in the constructor. Give
  the window content that scrolls, so that whatever is fitted to a short screen
  keeps its buttons outside the scroller.
- **Grid definitions:** never assign `RowDefinitions` or `ColumnDefinitions` on a live Grid directly; use `GridDefinitions.Set`. Test layout changes as transitions, not only as fresh windows: a stale cache only shows when one layout follows another.
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
  a drag onto a smaller screen, show from the tray, caret placement and
  dialogs; `SettingsNarrowHeadlessTests` covers the hotkey rows;
  `WindowPlacementHeadlessTests` covers restoring the
  placement.
- **Screenshots:** `LayoutSurveyCapture` renders every surface at seven
  working areas when `CLIPTHROUGH_LAYOUT_CAPTURE_DIR` is set. Run it before and
  after any layout change.
