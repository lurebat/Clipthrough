---
tags: [reactiveui, viewmodel, ui-state, bindings]
version: v0.17.1
severity: p0
status: active
---

# RaiseAndSetIfChanged returns the value, not whether it changed

## Problem

After first-run "Create library", and after every manual unlock of an
encrypted library, the main window stayed blank. Capture kept running in the
background, so the app looked alive but showed nothing. A restart got past the
welcome screen. It did not help an encrypted library without a remembered
password, because every launch goes through the unlock prompt. (audit F-46)

The setters for the two full-window overlays read naturally:

```csharp
private set
{
    if (!this.RaiseAndSetIfChanged(ref _isWelcomeOpen, value))
    {
        return;
    }

    this.RaisePropertyChanged(nameof(IsMainWorkspaceVisible));
}
```

The view model was right all along: `IsMainWorkspaceVisible` read `true`. The
workspace grid bound to it was still `IsVisible=False`, because nothing told
the binding to read the property again.

## Root cause

ReactiveUI's `RaiseAndSetIfChanged<TObj, TRet>` returns `TRet`, the **new
value**. It does not return a "did it change" flag. For a `bool` field the
misuse compiles, because the new value is itself a `bool`, so the guard means
"return early whenever the new value is false". Closing an overlay sets its
flag to `false`, so the dependent notification never fired.

For any other field type the same line fails to compile. That is why the
mistake can only hide in `bool` setters, and why reading the code does not
catch it: the name promises a boolean answer, and the type checker agrees.

The guard dates from before `IsMainWorkspaceVisible` was bound to anything.
While it was unbound, the missing notification had no visible effect. Binding
the grid's `IsVisible` to it is what exposed the bug.

## Solution

Compare against the field before setting, and treat the call as a statement:

```csharp
if (_isWelcomeOpen == value)
{
    return;
}

this.RaiseAndSetIfChanged(ref _isWelcomeOpen, value);
this.RaisePropertyChanged(nameof(IsMainWorkspaceVisible));
```

The call's result is never needed. The backing field already holds the value.

## Prevention

- `RaiseAndSetIfChangedUsageTests` scans every `.cs` file under `Clipthrough/`.
  It fails when the result of `RaiseAndSetIfChanged` is consumed, whether in an
  `if`, an assignment, a `return`, an expression-bodied member other than a
  setter, or an expression continued from the previous line. The only allowed
  forms are a plain statement or the body of a `set =>`. Its theory cases pin
  the detector itself, so a loosened regex shows up as a red test rather than a
  quiet pass.
- `MainWorkspaceVisibilityHeadlessTests` drives both transitions through the
  real commands on the real `MainWindow`: Create library from the welcome
  screen, and Unlock from the password prompt. It then asserts that the
  workspace grid, the top menu and the search box are *effectively visible*.
  Asserting on `IsMainWorkspaceVisible` would not catch this bug: that property
  was correct the whole time the window was blank.
- Both setters have a mutant in `Clipthrough.Tests/Mutation/mutants.json` that
  restores the old guard.
