using System;
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
    // When the control is hidden (one of the three stacked editors in the
    // clip preview), there's no point paying for a full TextDocument
    // replacement on every keystroke — the user can't see it. Stash the
    // latest text and flush it on next visibility change instead.
    private string? _pendingHiddenText;
    private bool _hasPendingHiddenText;

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
        this.GetObservable(IsReadOnlyProperty).Subscribe(v => _editor.IsReadOnly = v);
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
        ApplyTheme();
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
            if (_editor.Text != newText)
            {
                _editor.Text = newText ?? string.Empty;
            }
        }
        finally
        {
            _isSyncingText = false;
        }
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
            if (_editor.Text != pending)
            {
                _editor.Text = pending ?? string.Empty;
            }
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