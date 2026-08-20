using System;
using System.Diagnostics;
using Clipthrough.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Clipthrough.Controls;

/// <summary>
/// A text editor with optional TextMate syntax highlighting and theme reactivity.
/// Drop-in replacement for TextBox in content editing scenarios.
/// </summary>
public sealed class SyntaxTextEditor : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SyntaxTextEditor, string?>(nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<SyntaxTextEditor, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<string> SyntaxHintProperty =
        AvaloniaProperty.Register<SyntaxTextEditor, string>(nameof(SyntaxHint), string.Empty);

    public static readonly StyledProperty<int> SelectionStartProperty =
        AvaloniaProperty.Register<SyntaxTextEditor, int>(nameof(SelectionStart), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> SelectionLengthProperty =
        AvaloniaProperty.Register<SyntaxTextEditor, int>(nameof(SelectionLength), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private readonly TextEditor _editor;
    private TextMate.Installation? _textMateInstall;
    private readonly RegistryOptions _darkRegistry = new(ThemeName.DarkPlus);
    private readonly RegistryOptions _lightRegistry = new(ThemeName.LightPlus);
    private bool _isSyncingText;
    private bool _grammarUpdateScheduled;
    private bool _isAttached;
    // When the control is hidden (one of the three stacked editors in the
    // clip preview), there's no point paying for a full TextDocument
    // replacement on every keystroke — the user can't see it. Stash the
    // latest text and flush it on next visibility change instead.
    private string? _pendingHiddenText;
    private bool _hasPendingHiddenText;

    /// <summary>
    /// True while the editor is showing a shortened stand-in because the real
    /// content has a run too long to lay out. Forces read-only so the stand-in
    /// can never be saved over the clip it stands in for.
    /// </summary>
    private bool _isShowingShortenedText;

    /// <summary>
    /// Longest run without a line break that this control will hand to
    /// AvaloniaEdit.
    /// </summary>
    /// <remarks>
    /// AvaloniaEdit virtualises per *document line*, so a clip with many lines
    /// is flat in cost no matter how large, while a clip that is one enormous
    /// line defeats virtualisation entirely - the single visual line covering
    /// the viewport has to be laid out in full, on the UI thread, on every
    /// selection change.
    ///
    /// Measured in review round 2: linear at about 18 us per character, 2.00x
    /// per doubling across three doublings. A one-line clip of the library's
    /// mean 8,883 characters costs ~150 ms per arrow key; 200,000 characters
    /// costs 3.5 s; the largest clip in the reporting user's library, 766,983
    /// characters, extrapolates to about 13 s. Those are headless lower bounds.
    ///
    /// Word wrap is NOT the cause. That was the first hypothesis and the control
    /// disproved it: turning wrapping off removed about 4% and left the curve
    /// cleanly linear. A fix that only set WordWrap=false would have achieved
    /// nothing.
    ///
    /// The value matches <c>RichDocumentView.MaxUnbrokenRunChars</c>, which
    /// guards the rich-text pane against the same failure. That guard existing
    /// while the plain-text pane had none - though plain text is 852 of 1,638
    /// clips - is what identified this as an oversight rather than a cost
    /// inherent to showing large clips.
    /// </remarks>
    private const int MaxUnbrokenRunChars = 10_000;

    /// <summary>
    /// True when the pane is showing a shortened stand-in because the clip has a
    /// line too long to lay out, and is therefore read-only regardless of
    /// <see cref="IsReadOnly"/>.
    /// </summary>
    /// <remarks>
    /// Public because it is a real state of the control rather than a test hook:
    /// a caller that wants to explain the truncation in the surrounding UI needs
    /// to know it is happening.
    /// </remarks>
    public bool IsShowingShortenedText => _isShowingShortenedText;

    /// <summary>
    /// Whether the underlying editor currently refuses edits. This is the state
    /// that actually protects the clip, and it is not the same as
    /// <see cref="IsReadOnly"/>, which is the caller's request.
    /// </summary>
    internal bool IsEffectivelyReadOnly => _editor.IsReadOnly;

    public SyntaxTextEditor()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = false,
            WordWrap = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 13,
        };

        Content = _editor;

        _editor.TextChanged += OnEditorTextChanged;
        _editor.TextArea.SelectionChanged += OnEditorSelectionChanged;

        // AvaloniaEdit consumes Tab inside TextArea's own KeyDown and marks it
        // handled, so Avalonia's navigation - which runs later, on the TopLevel
        // bubble handler, and only for unhandled keys - never saw it. Focus
        // could then never leave the editor by keyboard: a WCAG 2.1.2 keyboard
        // trap. All of that consumption sits behind this one option, so turning
        // it off hands Tab and Shift+Tab back to normal focus traversal.
        _editor.Options.AcceptsTab = false;

        // Ctrl+Tab still has to be claimed before navigation sees it, and the
        // tunnel phase is the one point that runs strictly before both.
        _editor.TextArea.AddHandler(KeyDownEvent, OnTextAreaKeyDownTunnel, RoutingStrategies.Tunnel);

        this.GetObservable(TextProperty).Subscribe(OnTextPropertyChanged);
        this.GetObservable(IsReadOnlyProperty).Subscribe(v => _editor.IsReadOnly = v || _isShowingShortenedText);
        this.GetObservable(SyntaxHintProperty).Subscribe(_ => ScheduleGrammarUpdate());
        this.GetObservable(IsVisibleProperty).Subscribe(OnIsVisibleChanged);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int SelectionStart
    {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public int SelectionLength
    {
        get => GetValue(SelectionLengthProperty);
        set => SetValue(SelectionLengthProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>
    /// File extension hint for syntax highlighting (e.g. ".html", ".xml", ".json").
    /// Empty string disables syntax highlighting.
    /// </summary>
    public string SyntaxHint
    {
        get => GetValue(SyntaxHintProperty);
        set => SetValue(SyntaxHintProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        ApplyTheme();
    }

    /// <summary>
    /// A TextMate installation owns a <c>TMModel</c>, and a <c>TMModel</c> owns a
    /// running tokenizer thread. A running thread is a GC root, so an
    /// installation that is never disposed pins this control, its editor and its
    /// document for the life of the process — one leaked thread and one leaked
    /// document per editor that was ever shown, and the main window alone hosts
    /// two of these.
    ///
    /// Nothing else covers it: <see cref="ApplyTheme"/> disposes the previous
    /// installation, but it only runs on attach or on a theme change, and
    /// neither happens to a window that is simply closed.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _textMateInstall?.Dispose();
        _textMateInstall = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name == "ActualThemeVariant")
        {
            ApplyTheme();
        }
    }

    private void OnTextPropertyChanged(string? newText)
    {
        if (_isSyncingText)
        {
            return;
        }

        // Defer the (expensive) Document replacement while we're hidden.
        // The clip preview stacks three SyntaxTextEditors with different
        // IsVisible bindings and only one shows at a time, so updating the
        // other two on every keystroke is pure overhead.
        if (!IsVisible)
        {
            _pendingHiddenText = newText;
            _hasPendingHiddenText = true;
            return;
        }

        _isSyncingText = true;
        try
        {
            var displayText = ShortenIfUnlayoutable(newText);
            if (_editor.Text != displayText)
            {
                _editor.Text = displayText;
            }

            _editor.IsReadOnly = IsReadOnly || _isShowingShortenedText;
        }
        finally
        {
            _isSyncingText = false;
        }
    }

    /// <summary>
    /// Returns the text to lay out, shortened when the real content contains a
    /// run longer than <see cref="MaxUnbrokenRunChars"/>, and records whether it
    /// did.
    /// </summary>
    /// <remarks>
    /// Shortening rather than inserting break opportunities: a break-inserted
    /// view shows everything but lies about the content, so selecting and
    /// copying out of the pane would yield text with newlines the clip does not
    /// have. A visibly truncated view is honest about being partial, and the
    /// clip itself is untouched - the copy commands still read the stored clip,
    /// not this control.
    ///
    /// The pane is forced read-only whenever this fires, so the shortened stand
    /// -in can never be committed over the real clip.
    /// </remarks>
    private string ShortenIfUnlayoutable(string? text)
    {
        if (string.IsNullOrEmpty(text) || LongestUnbrokenRun(text) <= MaxUnbrokenRunChars)
        {
            _isShowingShortenedText = false;
            return text ?? string.Empty;
        }

        _isShowingShortenedText = true;
        Trace.TraceWarning(
            $"Clip text contains a {LongestUnbrokenRun(text):N0}-character run with no line break; "
            + "showing a shortened, read-only view because laying it out would stall the UI thread.");

        return text[..Math.Min(text.Length, MaxUnbrokenRunChars)] + AppText.EditorLineTooLongSuffix;
    }

    /// <summary>Length of the longest stretch containing no line break.</summary>
    private static int LongestUnbrokenRun(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var ch in text)
        {
            if (ch is '\n' or '\r')
            {
                current = 0;
                continue;
            }

            current++;
            if (current > longest)
            {
                longest = current;
            }

            // Nothing above the cap changes the decision, so stop walking a
            // 767 KB clip once the answer is settled.
            if (longest > MaxUnbrokenRunChars)
            {
                return longest;
            }
        }

        return longest;
    }

    private void OnIsVisibleChanged(bool isVisible)
    {
        if (!isVisible || !_hasPendingHiddenText)
        {
            return;
        }

        // We just became visible and missed a text update while hidden — apply
        // the latest stashed value now so the editor catches up.
        var pending = _pendingHiddenText;
        _hasPendingHiddenText = false;
        _pendingHiddenText = null;

        _isSyncingText = true;
        try
        {
            // Compare against what will actually be assigned. Comparing the raw
            // pending text against an already-shortened editor would never match,
            // so a shortened clip would be re-laid-out on every visibility flip.
            var displayText = ShortenIfUnlayoutable(pending);
            if (_editor.Text != displayText)
            {
                _editor.Text = displayText;
            }

            _editor.IsReadOnly = IsReadOnly || _isShowingShortenedText;
        }
        finally
        {
            _isSyncingText = false;
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isSyncingText)
        {
            return;
        }

        _isSyncingText = true;
        try
        {
            Text = _editor.Text;
        }
        finally
        {
            _isSyncingText = false;
        }
    }

    private void OnEditorSelectionChanged(object? sender, EventArgs e)
    {
        SelectionStart = _editor.SelectionStart;
        SelectionLength = _editor.SelectionLength;
    }

    /// <summary>
    /// Ctrl+Tab inserts a literal tab. Plain Tab and Shift+Tab are left alone so
    /// they traverse focus like any other control, which is what
    /// <c>Options.AcceptsTab = false</c> in the constructor arranges.
    /// </summary>
    /// <remarks>
    /// This is the VS Code convention. It keeps stray indentation out of clip
    /// text by default while leaving a deliberate way to type a tab, and it
    /// costs nothing in reach: Ctrl+Tab is not otherwise usable from here,
    /// because the window's own Ctrl+Tab viewer-mode shortcut already declines
    /// to act on keys coming from a multi-line editor.
    /// </remarks>
    private void OnTextAreaKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || e.Handled || e.KeyModifiers != KeyModifiers.Control)
        {
            return;
        }

        // Claim the key before focus traversal sees it, then insert the tab.
        e.Handled = true;
        if (_editor.IsReadOnly)
        {
            return;
        }

        var textArea = _editor.TextArea;
        textArea.Selection.ReplaceSelectionWithText("\t");
        textArea.Caret.BringCaretToView();
    }

    private void ApplyTheme()
    {
        // A theme change can reach a control that is off the visual tree, and
        // installing TextMate there would resurrect the tokenizer thread that
        // OnDetachedFromVisualTree just stopped. Re-attaching calls back into
        // here, so nothing is lost by skipping it.
        if (!_isAttached)
        {
            return;
        }

        var isDark = ActualThemeVariant != ThemeVariant.Light;

        _editor.Background = isDark
            ? new SolidColorBrush(Color.Parse("#1E293B"))
            : new SolidColorBrush(Color.Parse("#F8FAFC"));
        _editor.Foreground = isDark
            ? new SolidColorBrush(Color.Parse("#E2E8F0"))
            : new SolidColorBrush(Color.Parse("#0F172A"));

        _textMateInstall?.Dispose();
        var registry = isDark ? _darkRegistry : _lightRegistry;
        _textMateInstall = _editor.InstallTextMate(registry);
        // Safe to call directly — fresh installation has no running tokenizer.
        ApplyGrammar();
    }

    private void ScheduleGrammarUpdate()
    {
        if (_grammarUpdateScheduled) return;
        _grammarUpdateScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _grammarUpdateScheduled = false;
            ReinstallAndApplyGrammar();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Disposes the current TextMate installation (stopping its tokenizer
    /// thread) and creates a fresh one before applying the grammar.  This
    /// prevents an ABBA deadlock: <c>TextMateColoringTransformer.SetGrammar</c>
    /// acquires the transformer lock then needs the TMModel lock, while the
    /// tokenizer's <c>Emit</c> path holds the TMModel lock and needs the
    /// transformer lock in <c>ModelTokensChanged</c>.  Disposing first stops
    /// the tokenizer so it cannot hold any locks during <c>SetGrammar</c>.
    /// </summary>
    private void ReinstallAndApplyGrammar()
    {
        if (_textMateInstall is null) return;

        var isDark = ActualThemeVariant != ThemeVariant.Light;
        var registry = isDark ? _darkRegistry : _lightRegistry;

        _textMateInstall.Dispose();
        _textMateInstall = _editor.InstallTextMate(registry);
        ApplyGrammar();
    }

    private void ApplyGrammar()
    {
        if (_textMateInstall is null)
        {
            return;
        }

        var hint = SyntaxHint;
        if (string.IsNullOrEmpty(hint))
        {
            _textMateInstall.SetGrammar(null);
            return;
        }

        var registry = ActualThemeVariant != ThemeVariant.Light ? _darkRegistry : _lightRegistry;
        var lang = registry.GetLanguageByExtension(hint);
        if (lang is not null)
        {
            _textMateInstall.SetGrammar(registry.GetScopeByLanguageId(lang.Id));
        }
        else
        {
            _textMateInstall.SetGrammar(null);
        }
    }
}