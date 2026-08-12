using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using Clipthrough.Controls;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The clip content editor was a WCAG 2.1.2 keyboard trap. AvaloniaEdit
/// consumes Tab inside <c>TextArea</c>'s own KeyDown - there is no Tab
/// KeyBinding to remove - and marks it handled, so Avalonia's navigation, which
/// runs later on the TopLevel bubble handler and only for unhandled keys, never
/// saw it. Once focus entered the editor, Tab inserted a tab character forever
/// and the only way out was the mouse.
///
/// Every test here needs a second focusable control: a window with one control
/// keeps focus on it regardless, so it would pass even with the trap present.
/// </summary>
public sealed class SyntaxTextEditorHeadlessTests
{
    [AvaloniaFact]
    public void Tab_MovesFocusOutOfTheEditor()
    {
        using var fixture = EditorFixture.Create();
        fixture.FocusEditor();

        fixture.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();

        Assert.False(fixture.Editor.IsKeyboardFocusWithin, "Focus is still trapped inside the editor.");
        Assert.True(fixture.After.IsFocused);
        Assert.Equal("\tabc", fixture.Editor.Text);
    }

    /// <summary>
    /// Shift+Tab must navigate, not unindent. AvaloniaEdit's Shift+Tab handler
    /// calls Document.Remove to strip one indentation level, so if the editor
    /// still consumed the key it would silently destroy the user's leading
    /// whitespace - and that edit flows through TextChanged into the clip and
    /// gets persisted on the next selection change. The fixture text is
    /// indented on purpose: with unindented text there is nothing to strip and
    /// this assertion would pass either way.
    /// </summary>
    [AvaloniaFact]
    public void ShiftTab_MovesFocusBackwardsAndLeavesIndentationAlone()
    {
        using var fixture = EditorFixture.Create();
        fixture.FocusEditor();

        fixture.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("\tabc", fixture.Editor.Text);
        Assert.False(fixture.Editor.IsKeyboardFocusWithin);
    }

    /// <summary>
    /// Ctrl+Tab is the deliberate way to type a tab. Asserting the exact text
    /// rather than "contains a tab" also pins that it is inserted once - a
    /// double insertion would mean both this handler and the editor ran.
    /// </summary>
    [AvaloniaFact]
    public void CtrlTab_InsertsALiteralTabAndKeepsFocus()
    {
        using var fixture = EditorFixture.Create();
        fixture.FocusEditor();

        fixture.Window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();

        Assert.True(fixture.Editor.IsKeyboardFocusWithin, "Ctrl+Tab must not move focus.");
        Assert.Equal("\t\tabc", fixture.Editor.Text);
    }

    /// <summary>
    /// A read-only editor still has to release Tab, and must not gain text from
    /// the deliberate-insert path.
    /// </summary>
    [AvaloniaFact]
    public void ReadOnlyEditor_ReleasesTabAndRejectsCtrlTabInsertion()
    {
        using var fixture = EditorFixture.Create();
        fixture.Editor.IsReadOnly = true;
        Dispatcher.UIThread.RunJobs();
        fixture.FocusEditor();

        fixture.Window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("\tabc", fixture.Editor.Text);

        fixture.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();
        Assert.False(fixture.Editor.IsKeyboardFocusWithin);
    }
    /// <summary>
    /// The synthetic fixture above cannot catch the case that actually mattered:
    /// in the real window the editor is the *last* tab stop, and Avalonia's ring
    /// does not cycle through the focus manager, so TryMoveFocus(Next) fails
    /// there and the editor would keep the key. This walks the real MainWindow
    /// ring and asserts it comes back around instead of parking on the editor.
    /// </summary>
    [AvaloniaFact]
    public void TabRingInTheRealWindowCyclesInsteadOfStoppingOnTheEditor()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        Dispatcher.UIThread.RunJobs();

        // The editor only joins the tab ring once a clip is actually selected.
        // Tab used to select one as a side effect of jumping into the list; it
        // no longer does, so select one explicitly rather than relying on that.
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[0];
        Dispatcher.UIThread.RunJobs();

        harness.FocusSearchBox();
        Dispatcher.UIThread.RunJobs();

        var focusManager = TopLevel.GetTopLevel(harness.Window)!.FocusManager!;
        var reachedEditor = false;
        var returnedToSearch = false;

        // Long enough to lap the ring twice even if stops are added later.
        for (var i = 0; i < 80; i++)
        {
            harness.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            Dispatcher.UIThread.RunJobs();

            var focused = focusManager.GetFocusedElement() as Control;
            if (focused is null)
            {
                continue;
            }

            if (focused.FindAncestorOfType<SyntaxTextEditor>() is not null || focused is TextArea)
            {
                reachedEditor = true;
                continue;
            }

            if (reachedEditor && focused.Name == "SearchTextBox")
            {
                returnedToSearch = true;
                break;
            }
        }

        Assert.True(reachedEditor, "Tab never reached the content editor, so this test proves nothing.");
        Assert.True(returnedToSearch, "Focus never left the content editor - Tab is trapped there again.");
    }

    /// <summary>
    /// A TextMate installation owns a TMModel, which owns a running tokenizer
    /// thread — a GC root that pins the control, its editor and its document.
    /// The control used to install one on attach and never dispose it, so every
    /// window that showed an editor leaked a thread for the life of the process.
    /// </summary>
    [AvaloniaFact]
    public void ClosingTheWindowDisposesTheTextMateInstallation()
    {
        SyntaxTextEditor editor;
        TextMate.Installation installation;

        using (var fixture = EditorFixture.Create())
        {
            editor = fixture.Editor;
            installation = GetInstallation(editor)!;
            Assert.NotNull(installation);
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Null(GetInstallation(editor));

        // Nulling the field is not the contract - stopping the tokenizer is.
        // Only a genuinely disposed installation throws here.
        Assert.Throws<ObjectDisposedException>(() => installation.SetGrammar(null));
    }

    /// <summary>
    /// Two paths can install TextMate after teardown: a theme change routed
    /// through <c>ApplyTheme</c>, and a queued grammar update. Either one would
    /// restart the tokenizer thread on a control nothing can detach a second
    /// time, turning the leak back on permanently.
    /// </summary>
    [AvaloniaFact]
    public void NothingReinstallsTextMateAfterDetach()
    {
        SyntaxTextEditor editor;

        using (var fixture = EditorFixture.Create())
        {
            editor = fixture.Editor;
            Assert.NotNull(GetInstallation(editor));
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Null(GetInstallation(editor));

        // Path 1: a theme change reaching a detached control.
        typeof(SyntaxTextEditor)
            .GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, null);
        Assert.Null(GetInstallation(editor));

        // Path 2: a grammar update posted before detach, or a hint set after it.
        editor.SyntaxHint = ".json";
        Dispatcher.UIThread.RunJobs();
        Assert.Null(GetInstallation(editor));
    }

    private static TextMate.Installation? GetInstallation(SyntaxTextEditor editor)
        => (TextMate.Installation?)typeof(SyntaxTextEditor)
            .GetField("_textMateInstall", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor);
    private sealed class EditorFixture : System.IDisposable
    {
        private EditorFixture(Window window, Button before, SyntaxTextEditor editor, Button after)
        {
            Window = window;
            Before = before;
            Editor = editor;
            After = after;
        }

        public Window Window { get; }

        public Button Before { get; }

        public SyntaxTextEditor Editor { get; }

        public Button After { get; }

        public static EditorFixture Create()
        {
            var before = new Button { Content = "before" };
            var editor = new SyntaxTextEditor { Text = "\tabc" };
            var after = new Button { Content = "after" };

            var window = new Window
            {
                Width = 400,
                Height = 300,
                Content = new StackPanel { Children = { before, editor, after } },
            };

            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();

            return new EditorFixture(window, before, editor, after);
        }

        public void FocusEditor()
        {
            // Focusing the SyntaxTextEditor wrapper is not enough: focus lands on
            // the UserControl, and the trap only manifests once the inner
            // TextArea has it. Tab from the button ahead of it walks inward the
            // same way a user would.
            Before.Focus();
            Dispatcher.UIThread.RunJobs();

            for (var i = 0; i < 5 && !Editor.IsKeyboardFocusWithin; i++)
            {
                Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(Editor.IsKeyboardFocusWithin, "Focus never reached the editor; the test would not exercise the trap.");
        }

        public void Dispose()
        {
            try { Window.Close(); } catch { /* test teardown */ }
        }
    }
}
