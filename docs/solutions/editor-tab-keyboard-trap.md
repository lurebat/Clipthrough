---
tags: [avalonia, avaloniaedit, focus, keyboard, accessibility]
version: 2
severity: high
---

# The content editor swallowed Tab and trapped keyboard focus

## Symptom

Once focus reached the clip content editor (`SyntaxTextEditor`, wrapping
AvaloniaEdit), Tab inserted a tab character instead of moving on. A
keyboard-only user could enter the editor and never leave it without reaching
for the mouse - a WCAG 2.1.2 "no keyboard trap" failure.

Worse, and less obvious: **Shift+Tab silently corrupted the clip.** AvaloniaEdit
maps Shift+Tab to unindent, which calls `Document.Remove` to strip one
indentation level from the current line. For an indented text clip - JSON, XML,
code, exactly what the syntax highlighting exists for - pressing Shift+Tab to
navigate backwards removed the user's leading whitespace instead. That edit
flows `TextChanged -> OnEditorTextChanged -> Text -> EditedClipText` and is
persisted by `CommitEditedClipOnSelectionChangeAsync` on the next selection
change.

## The fix

```csharp
_editor.Options.AcceptsTab = false;
```

All of AvaloniaEdit's Tab handling sits behind this one option:
`TextArea.OnKeyDown` only consumes Tab when `Options.AcceptsTab` is true. With
it off, both Tab and Shift+Tab are left unhandled, so Avalonia's own
`KeyboardNavigationHandler` - which runs on the TopLevel bubble handler, for
unhandled keys only - performs normal focus traversal in both directions,
including wrapping the ring.

Ctrl+Tab is then claimed in a **tunnel**-phase handler on `_editor.TextArea` to
insert a literal tab, matching the VS Code convention. It has to be the tunnel
phase so it pre-empts focus traversal. This costs nothing in reach: the window's
own Ctrl+Tab viewer-mode shortcut already declines to act on keys originating in
a multi-line editor.

## Dead ends - do not spend time here again

Everything below was measured empirically against Avalonia 12.0.1 /
AvaloniaEdit 12.0.0 before `AcceptsTab` was found. None of it is needed, but the
findings are correct and cost real time to establish.

- **"Remove the Tab `KeyBinding`."** There is none.
  `TextArea.DefaultInputHandler.Editing` and `.CaretNavigation` have no
  `KeyBinding` or `CommandBinding` for `Key.Tab`. Tab is consumed directly in
  `TextArea.OnKeyDown`.
- **"Handle Tab in a normal (bubble) handler on the wrapper."** Too late.
  Measured routing is `TUNNEL handled=False` then `BUBBLE handled=True`.
- **"Make the wrapper focusable / a `TabNavigation=Once` group."** This makes the
  editor untypeable: Tab stops on the wrapper and never descends into the
  `TextArea`, so the caret never appears.
- **Moving focus by hand through `IFocusManager`.** Viable but unnecessary, and
  it is a minefield:

  | Call | Result |
  | --- | --- |
  | `FindNextElement(Previous)` from the `TextArea`, the wrapper, or a plain `Button` | `null` |
  | `TryMoveFocus(Previous)` between two plain `Button`s | returns **`true`**, focus **does not move** |
  | `TryMoveFocus(Next)` | works, but returns `false` on the last stop - the ring does not cycle |
  | `element.Focus(NavigationMethod.Tab)` on a known target | works |

  The Shift+Tab *keystroke* navigates correctly between ordinary controls even
  though the `IFocusManager` API for it is inert, because the keystroke path
  goes through Avalonia's internal `KeyboardNavigationHandler` instead. That
  mismatch between keystroke and API makes this area very confusing to debug.

  A hand-rolled traversal also has to reimplement `KeyboardNavigation.TabIndex`,
  `TabNavigation` scoping and `TabOnceActiveElement`. Nothing in this app sets
  those today, so a naive visual-tree walk *appears* to work - and would quietly
  diverge the moment someone adds a `TabIndex`.

## Invariants to preserve

1. Keep `Options.AcceptsTab = false`. It is the whole fix; without it both the
   trap and the Shift+Tab corruption return.
2. The Ctrl+Tab handler must stay on `TextArea` in the **tunnel** phase.
3. Ctrl+Tab must not insert into a read-only editor.

## Testing notes

`Clipthrough.Tests/Headless/SyntaxTextEditorHeadlessTests.cs`. Mutation-verified:
flipping `AcceptsTab` back to `true` fails 4 of the 5 tests.

Traps specific to testing this:

- **Seed the fixture with indented text (`"\tabc"`).** The Shift+Tab test with
  unindented text is vacuous - there is nothing for unindent to remove, so it
  passes whether or not the bug is present. This is exactly how the corruption
  survived an earlier round of tests.
- **Assert exact text, not `Contains("\t")`,** for the Ctrl+Tab insertion, so a
  double insertion (handler *and* editor both running) is caught.
- A synthetic two-control fixture is not sufficient on its own. In the real
  window the editor is the **last** tab stop, which is a materially different
  case; `TabRingInTheRealWindowCyclesInsteadOfStoppingOnTheEditor` drives the
  real `MainWindow` through `MainWindowTestHarness` and asserts the ring comes
  back round.
- Headless focus tests are delicate: assert **before** `window.Close()` (closing
  clears focus), and show only one window per test method (several `Show()`
  calls in one method hangs the run).
