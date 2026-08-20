using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Controls;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.ViewModels;

namespace Clipthrough.Views;

public partial class MainWindow : Window
{
    private ListBox? m_clipsListBox;
    private ScrollViewer? m_clipListScrollViewer;
    private SessionLogsViewModel? m_subscribedSessionLogs;
    private SessionLogsWindow? m_sessionLogsWindow;
    private MainWindowViewModel? m_subscribedViewModel;
    private SettingsWindow? m_settingsWindow;
    private AiPromptWindow? m_aiPromptWindow;
    private ISystemInteractionService? m_systemInteractionService;
    private bool m_focusClipOnNextActivation;
    private bool m_focusClipOnNextSelectionChange;
    private int m_focusRequestGeneration;
    private Point? m_dragStartPoint;
    private ClipItemViewModel? m_dragCandidateClip;
    private PointerPressedEventArgs? m_dragPressedArgs;
    private bool m_dragInProgress;
    private const double DragThreshold = 4.0;

    /// <summary>
    /// Parsed once from a literal rather than per menu rebuild inside a
    /// swallow-everything try/catch. A gesture literal either parses on every
    /// run or on none, so a failure here is a coding error that should be loud
    /// at startup, not a menu that silently loses its shortcut hint.
    /// </summary>
    private static readonly KeyGesture s_aiTransformGesture = KeyGesture.Parse("Ctrl+I");

    public MainWindow() : this(null) { }

    public MainWindow(ISystemInteractionService? systemInteractionService)
    {
        m_systemInteractionService = systemInteractionService;
        InitializeComponent();
        Opened += OnOpened;
        Activated += OnActivated;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (m_clipListScrollViewer is not null)
        {
            FocusInitialPopupTarget();
            return;
        }

        m_clipsListBox = this.FindControl<ListBox>("ClipsListBox");
        if (m_clipsListBox is not null)
        {
            m_clipsListBox.AddHandler(InputElement.DoubleTappedEvent, OnClipsListDoubleTapped, RoutingStrategies.Bubble);
            m_clipsListBox.AddHandler(InputElement.PointerPressedEvent, OnClipsListPointerPressed, RoutingStrategies.Tunnel);
            m_clipsListBox.AddHandler(InputElement.PointerMovedEvent, OnClipsListPointerMoved, RoutingStrategies.Tunnel);
            m_clipsListBox.AddHandler(InputElement.PointerReleasedEvent, OnClipsListPointerReleased, RoutingStrategies.Tunnel);
        }

        // Drag-in: accept files/images/text dropped onto the popup. The whole
        // window is a drop target; ImportDroppedDataAsync routes through the
        // normal capture path and stamps ImportKind="drag_drop".
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnPopupDragOver);
        AddHandler(DragDrop.DropEvent, OnPopupDrop);

        TryConnectClipListScrollViewer();

        PopulateTransformMenus();

        var searchBox = GetSearchBox();
        if (searchBox is not null)
        {
            searchBox.GotFocus += OnSearchBoxGotFocus;
            searchBox.LostFocus += OnSearchBoxLostFocus;
        }

        var menu = this.FindControl<Menu>("TopMenu");
        if (menu is not null)
        {
            menu.GotFocus += OnTopMenuGotFocus;
        }

        FocusInitialPopupTarget();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (m_focusClipOnNextActivation)
        {
            m_focusClipOnNextActivation = false;
            FocusInitialPopupTarget();
        }
    }

    /// <summary>
    /// Lazily connects the clip list scroll viewer for scroll-to-load.
    /// Called on open and again after the password prompt is dismissed,
    /// because the ListBox may not have visual descendants while hidden.
    /// </summary>
    internal void TryConnectClipListScrollViewer()
    {
        if (m_clipListScrollViewer is not null)
        {
            return;
        }

        m_clipListScrollViewer = m_clipsListBox?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (m_clipListScrollViewer is not null)
        {
            m_clipListScrollViewer.ScrollChanged += OnClipListScrollChanged;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (m_subscribedSessionLogs is not null)
        {
            m_subscribedSessionLogs.PropertyChanged -= OnSessionLogsPropertyChanged;
            m_subscribedSessionLogs = null;
        }

        if (m_sessionLogsWindow is not null)
        {
            m_sessionLogsWindow.Closing -= OnSessionLogsWindowClosing;
            CloseChildWindow(m_sessionLogsWindow, "session logs");
            m_sessionLogsWindow = null;
        }

        if (m_subscribedViewModel is not null)
        {
            m_subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            m_subscribedViewModel.HelpRequested -= OnHelpRequested;
            m_subscribedViewModel.AboutRequested -= OnAboutRequested;
            m_subscribedViewModel.Dispose();
            m_subscribedViewModel = null;
        }

        if (m_settingsWindow is not null)
        {
            m_settingsWindow.Closing -= OnSettingsWindowClosing;
            CloseChildWindow(m_settingsWindow, "settings");
            m_settingsWindow = null;
        }

        if (m_aiPromptWindow is not null)
        {
            m_aiPromptWindow.Closing -= OnAiPromptWindowClosing;
            CloseChildWindow(m_aiPromptWindow, "AI prompt");
            m_aiPromptWindow = null;
        }

        if (m_clipListScrollViewer is null)
        {
            if (m_clipsListBox is not null)
            {
                m_clipsListBox.RemoveHandler(InputElement.DoubleTappedEvent, OnClipsListDoubleTapped);
                m_clipsListBox = null;
            }

            return;
        }

        m_clipListScrollViewer.ScrollChanged -= OnClipListScrollChanged;
        m_clipListScrollViewer = null;
        if (m_clipsListBox is not null)
        {
            m_clipsListBox.RemoveHandler(InputElement.DoubleTappedEvent, OnClipsListDoubleTapped);
            m_clipsListBox.RemoveHandler(InputElement.PointerPressedEvent, OnClipsListPointerPressed);
            m_clipsListBox.RemoveHandler(InputElement.PointerMovedEvent, OnClipsListPointerMoved);
            m_clipsListBox.RemoveHandler(InputElement.PointerReleasedEvent, OnClipsListPointerReleased);
            m_clipsListBox = null;
        }

        RemoveHandler(DragDrop.DragOverEvent, OnPopupDragOver);
        RemoveHandler(DragDrop.DropEvent, OnPopupDrop);
    }

    /// <summary>
    /// Closes a child window during teardown without letting its failure abort
    /// the rest of the teardown, which would strand the remaining child windows
    /// and their event subscriptions. The catch stays broad because a window
    /// close runs arbitrary <c>Closing</c> handlers and renderer shutdown, but
    /// the failure is traced so it reaches the session log instead of vanishing.
    /// </summary>
    internal static void CloseChildWindow(Window window, string name)
    {
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to close the {name} window during teardown: {ex}");
        }
    }

    private void OnClipListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (m_clipListScrollViewer is null || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsBusy || !viewModel.HasMoreResults)
        {
            return;
        }

        var remainingHeight = m_clipListScrollViewer.Extent.Height - (m_clipListScrollViewer.Offset.Y + m_clipListScrollViewer.Viewport.Height);
        if (remainingHeight > 180)
        {
            return;
        }

        viewModel.LoadMoreCommand.Execute().Subscribe();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // Fast path: a plain printable keystroke inside a text input control
        // cannot match any of our shortcut handlers (they all require either
        // a non-text source or a Ctrl/Alt modifier), so we can bail out before
        // the ten TryHandle* probes below run. This avoids ten visual-tree
        // walks per character of typing in the search box and clip editors.
        if (IsKeyEventFromTextInput(e) && IsPlainPrintableShortcut(e))
        {
            return;
        }

        if (TryRecoverFromTopMenuFocus(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleEditedClipShortcut(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleArrowKeyNavigation(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleEscapeKey(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleClipListShortcuts(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleClipIndexShortcut(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleClipRecopyShortcut(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleClipPasteShortcut(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleEnterToCopyShortcut(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        if (viewModel.TryHandleShortcut(e))
        {
            e.Handled = true;
        }

        // Type-to-filter is deliberately NOT handled here. Marking a printable
        // KeyDown handled suppresses the platform's TextInput event, which is the
        // only source of the character the user's keyboard layout actually
        // produced. See OnTextInput.
    }

    /// <summary>
    /// Type-to-filter. Runs on TextInput rather than KeyDown because
    /// <see cref="KeyEventArgs.Key"/> is a layout-independent virtual key code:
    /// the physical A key reports <see cref="Key.A"/> on Hebrew, Russian and
    /// Arabic layouts alike, so deriving a character from it always produced
    /// Latin. TextInput carries the text the platform actually composed, which
    /// also gives us dead keys, AltGr and IME composition for free.
    /// </summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (TryRedirectToSearchBox(e.Text, e.Source))
        {
            e.Handled = true;
        }
    }

    private bool TryRecoverFromTopMenuFocus(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down))
        {
            return false;
        }

        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        if (modifiers != KeyModifiers.None)
        {
            return false;
        }

        if (e.Source is not Avalonia.Visual source
            || source.GetSelfAndVisualAncestors().OfType<Menu>().FirstOrDefault(menu => menu.Name == "TopMenu") is not { } menu)
        {
            return false;
        }

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsSubMenuOpen = false;
        }

        if (viewModel.IsSettingsOpen || viewModel.IsWelcomeOpen || viewModel.IsPasswordPromptOpen
            || viewModel.IsAiPromptOpen || viewModel.SessionLogs.IsOpen)
        {
            return false;
        }

        if (viewModel.SelectedClip is null)
        {
            viewModel.SelectedClip = viewModel.GetDefaultAutoSelectedClip();
        }

        if (viewModel.SelectedClip is null)
        {
            FocusSearchBox();
        }
        else
        {
            FocusSelectedClipInList();
        }

        return true;
    }

    /// <summary>
    /// Arrow Down from search box moves focus into the clip list.
    /// Up/Down in the clip list navigates between clips.
    /// Home/End jump to the first/last clip.
    /// Ctrl+F focuses the search box from anywhere.
    /// </summary>
    private bool TryHandleArrowKeyNavigation(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);

        // Ctrl+F: focus search box from anywhere
        if (e.Key == Key.F && modifiers == KeyModifiers.Control)
        {
            FocusSearchBox();
            return true;
        }

        if (e.Key == Key.Back && modifiers == KeyModifiers.Control)
        {
            _ = viewModel.ClearSearchFilterAsync(forceRefresh: true);
            FocusSearchBox();
            return true;
        }

        if (e.Key == Key.O && modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            FocusSortBox();
            return true;
        }

        if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift)
            && TryGetShortcutNumber(e.Key) is { } contentTypeShortcut
            && viewModel.TrySelectContentTypeByShortcut(contentTypeShortcut))
        {
            return true;
        }

        // Don't interfere with arrow keys in multi-line text editors. The
        // single-line search box must fall through so Tab / Up / Down can move
        // focus into the clip list and walk the search history.
        if (IsKeyEventFromMultiLineEditor(e))
        {
            return false;
        }

        if (viewModel.IsSettingsOpen || viewModel.IsWelcomeOpen || viewModel.IsPasswordPromptOpen
            || viewModel.IsAiPromptOpen || viewModel.SessionLogs.IsOpen)
        {
            return false;
        }

        var searchBox = GetSearchBox();
        var isSearchFocused = searchBox?.IsKeyboardFocusWithin == true;

        // Ctrl+Up/Down are the explicit jump between the search box and the clip
        // list. They exist so plain Up/Down can behave the way an autocomplete
        // is expected to, without the list becoming unreachable while the
        // dropdown is showing - which is what forced the earlier compromise
        // below. Claimed before anything else so they work from the box, the
        // dropdown, and the list alike.
        //
        // These shadow a custom hotkey bound to the same chord, because this
        // handler runs before the configurable ones. Neither is a default and
        // the tradeoff is deliberate: navigation between the two panes has to
        // work from a standing start, whatever else is configured.
        if (modifiers == KeyModifiers.Control && e.Key is Key.Down or Key.Up)
        {
            if (e.Key == Key.Up)
            {
                FocusSearchBox();
                return true;
            }

            if (viewModel.Clips.Count > 0)
            {
                viewModel.SelectedClip ??= viewModel.GetDefaultAutoSelectedClip();
                FocusSelectedClipInList();
            }

            return true;
        }

        // Keys belonging to the open suggestion dropdown are claimed here rather than on
        // the list itself. The window's key handler is registered to *tunnel*
        // (see the constructor), so it sees every key before the focused control does:
        // a handler on the ListBox is unreachable for anything this method also acts on,
        // which is why Escape used to clear the whole search box instead of closing the
        // dropdown. Arrow keys are deliberately not claimed - the list moves its own
        // highlight with those.
        var suggestions = GetSearchSuggestionsList();
        if (suggestions?.IsKeyboardFocusWithin == true && modifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    ApplySearchSuggestion(suggestions, suggestions.SelectedItem as string);
                    return true;

                case Key.Escape:
                    FocusSearchBox();
                    return true;

                // Up off the top returns to the box rather than trapping focus in a list
                // the user arrowed into from above.
                case Key.Up when suggestions.SelectedIndex <= 0:
                    FocusSearchBox();
                    return true;
            }
        }

        if (isSearchFocused && modifiers == KeyModifiers.Alt && (e.Key == Key.Down || e.Key == Key.Up))
        {
            _ = viewModel.NavigateSearchHistoryAsync(e.Key == Key.Up ? -1 : 1);
            return true;
        }

        // Plain Up and Down both belong to the suggestion dropdown while it is
        // showing: Up reaches it from the box, Down walks into it, and inside it
        // the list moves its own highlight. Up with the dropdown closed still
        // cycles recent searches straight into the box, which is what Up meant
        // before the dropdown existed.
        //
        // This reverses an earlier decision, so the reasoning is worth keeping.
        // Down used to skip the dropdown and go to the clip list, because the
        // dropdown opens whenever the query substring-matches any past search -
        // which is most of the time - and sharing Down would have made looking
        // at the results unreachable exactly when the query resembled an old
        // one. That was a real problem with no good answer while Up and Down
        // were the only keys available. Ctrl+Down above is the answer: the list
        // is now always one chord away, so the dropdown can have the plain keys
        // and behave like every other autocomplete.
        if (isSearchFocused && modifiers == KeyModifiers.None && e.Key == Key.Up)
        {
            if (!viewModel.IsSearchSuggestionsOpen || !FocusSearchSuggestion(0))
            {
                _ = viewModel.NavigateSearchHistoryAsync(-1);
            }

            return true;
        }

        if (e.Key == Key.Tab && modifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))
        {
            viewModel.CycleSelectedViewerMode((modifiers & KeyModifiers.Shift) == KeyModifiers.Shift);
            return true;
        }

        // Plain Tab and Shift+Tab are deliberately not intercepted. They used to
        // jump straight between the search box and the clip list, which skipped
        // the filter toggles sitting visually between them and left those
        // controls reachable only by mouse. Tab now walks the window in visual
        // order like any other app; Down from the search box (below) is still
        // the one-key path into the list.

        // Down from the search box: into the suggestion dropdown while it is
        // showing, and into the clip list when it is not. With the dropdown
        // closed there is nothing for Down to navigate, so the one-key path into
        // the results is kept rather than made Ctrl-only.
        //
        // Deliberately ABOVE the empty-list bail-out below. The dropdown offers
        // past searches, which is most useful precisely when the current query
        // matches nothing - and that is exactly when Clips.Count is 0. Sitting
        // below the bail-out meant Down did nothing in the one case the user
        // most needed it, while Up still worked because it is handled earlier.
        // Found independently by two reviewers in round 2 (quality-opus Q3,
        // bugs-opus F5), against a change made the day before.
        if (e.Key == Key.Down && modifiers == KeyModifiers.None && isSearchFocused
            && viewModel.IsSearchSuggestionsOpen && FocusSearchSuggestion(0))
        {
            return true;
        }

        if (viewModel.Clips.Count == 0)
        {
            return false;
        }

        if (e.Key == Key.Down && modifiers == KeyModifiers.None && isSearchFocused)
        {
            if (viewModel.SelectedClip is null)
            {
                viewModel.SelectedClip = viewModel.GetDefaultAutoSelectedClip();
            }
            FocusSelectedClipInList();
            return true;
        }

        // Home/End: jump to first/last clip. Only from the clip list -- inside the
        // search box these keep their standard caret-movement meaning.
        if (modifiers == KeyModifiers.None && m_clipsListBox?.IsKeyboardFocusWithin == true)
        {
            if (e.Key == Key.Home)
            {
                viewModel.SelectedClip = viewModel.Clips[0];
                FocusSelectedClipInList();
                return true;
            }

            if (e.Key == Key.End)
            {
                viewModel.SelectedClip = viewModel.Clips[^1];
                FocusSelectedClipInList();
                return true;
            }
        }

        // Up/Down in clip list: navigate between clips
        if (m_clipsListBox?.IsKeyboardFocusWithin != true)
        {
            // Focus nowhere is a state this window really reaches: replacing a
            // row destroys its container and Avalonia leaves focus null rather
            // than moving it somewhere sensible, which is why the collection
            // -changed handler restores it. Until that restore runs, an arrow
            // key falls out of here unhandled and Avalonia's own directional
            // navigation decides where it goes - and the top menu sits above the
            // list, so "File" can take it. TryRecoverFromTopMenuFocus already
            // covers focus that has *arrived* in the menu; this stops the key
            // that puts it there.
            //
            // Asaf reported the menu stealing focus while holding Down. This is
            // the one hole visible in the code, not a reproduction: sustained
            // key repeat over a seeded list keeps focus on a ListBoxItem for
            // every press in the headless host, which has no real window
            // activation or menu access-key handling to reproduce it with.
            if (e.Key is Key.Up or Key.Down
                && modifiers == KeyModifiers.None
                && viewModel.Clips.Count > 0
                && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null)
            {
                viewModel.SelectedClip ??= viewModel.GetDefaultAutoSelectedClip();
                FocusSelectedClipInList();
                return true;
            }

            return false;
        }

        if (modifiers != KeyModifiers.None)
        {
            return false;
        }

        var currentIndex = viewModel.SelectedClip is not null ? viewModel.Clips.IndexOf(viewModel.SelectedClip) : -1;

        if (e.Key == Key.Down)
        {
            var nextIndex = currentIndex + 1;
            if (nextIndex < viewModel.Clips.Count)
            {
                viewModel.SelectedClip = viewModel.Clips[nextIndex];
                FocusSelectedClipInList();
            }
            return true;
        }

        if (e.Key == Key.Up)
        {
            // Index 0 -- and any selection that is no longer part of the list --
            // returns focus to the search box rather than swallowing the key.
            if (currentIndex <= 0)
            {
                FocusSearchBox();
                return true;
            }

            viewModel.SelectedClip = viewModel.Clips[currentIndex - 1];
            FocusSelectedClipInList();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Escape: if search text is non-empty, clear it and stay in search box.
    /// If search is empty and clip list is focused, move focus back to search box.
    /// </summary>
    private bool TryHandleEscapeKey(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return false;
        }

        if (viewModel.IsSettingsOpen || viewModel.IsWelcomeOpen || viewModel.IsPasswordPromptOpen
            || viewModel.IsAiPromptOpen || viewModel.SessionLogs.IsOpen)
        {
            return false;
        }

        // Don't capture Escape from multi-line text editors
        if (IsKeyEventFromMultiLineEditor(e))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(viewModel.SearchText))
        {
            viewModel.SearchText = string.Empty;
            FocusSearchBox();
            return true;
        }

        if (m_clipsListBox?.IsKeyboardFocusWithin == true)
        {
            FocusSearchBox();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Keyboard shortcuts when the clip list is focused:
    /// Delete — delete selected clip
    /// Space — toggle checkbox for multi-selection
    /// </summary>
    private bool TryHandleClipListShortcuts(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (m_clipsListBox?.IsKeyboardFocusWithin != true || viewModel.SelectedClip is null)
        {
            return false;
        }

        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);

        // Ctrl+A: toggle select/deselect all clips
        if (e.Key == Key.A && modifiers == KeyModifiers.Control)
        {
            if (viewModel.Clips.All(static c => c.IsChecked))
                viewModel.SelectNoClipsCommand.Execute().Subscribe();
            else
                viewModel.SelectAllClipsCommand.Execute().Subscribe();
            return true;
        }

        // Ctrl+Shift+C: copy selected clip as plain text
        if (e.Key == Key.C && modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            _ = viewModel.CopySelectedAsPlainTextAsync();
            return true;
        }

        // Ctrl+D: copy selected clip to clipboard without pasting or closing
        if (e.Key == Key.D && modifiers == KeyModifiers.Control)
        {
            viewModel.CopySelectedCommand.Execute().Subscribe();
            return true;
        }

        if (modifiers != KeyModifiers.None)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Delete:
                viewModel.DeleteSelectedCommand.Execute().Subscribe();
                return true;
            case Key.Space:
                viewModel.ToggleClipCheckedSelection(viewModel.SelectedClip);
                return true;
            default:
                return false;
        }
    }

    private void FocusSelectedClipInList()
    {
        if (m_clipsListBox is null)
        {
            return;
        }

        var generation = BeginFocusRequest();

        // Scroll now, and again once the selection has settled.
        //
        // The immediate call reads m_clipsListBox.SelectedItem, which arrives
        // through a two-way binding from the view model property the caller has
        // just assigned. Whenever that binding has already run the two agree and
        // the second scroll is a no-op; if it has not, the first scroll goes to
        // the row the user came *from*. Repeating it inside the same post that
        // moves focus removes the ordering question rather than reasoning about
        // it - and focus was already deferred for exactly this kind of reason.
        //
        // Not written against a reproduction: Asaf reported the list not
        // following the selection and this path is correct under every headless
        // variant tried - short lists, a virtualising 2,000-row list, a list
        // wheel-scrolled away from the selection, and key repeat. So this is the
        // one hazard visible in the code, not a demonstrated fix.
        ScrollSelectedClipIntoView();

        Dispatcher.UIThread.Post(() =>
        {
            if (m_focusRequestGeneration != generation || m_clipsListBox.SelectedIndex < 0)
            {
                return;
            }

            ScrollSelectedClipIntoView();

            var container = m_clipsListBox.ContainerFromIndex(m_clipsListBox.SelectedIndex);
            if (container is ListBoxItem item)
            {
                item.Focus();
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Scrolls to the selected row by index rather than by item, so it follows
    /// whatever the list itself has settled on.
    /// </summary>
    private void ScrollSelectedClipIntoView()
    {
        if (m_clipsListBox is { SelectedIndex: >= 0 } list)
        {
            list.ScrollIntoView(list.SelectedIndex);
        }
    }

    /// <summary>
    /// Focus moves are scheduled, not immediate, and several of them retry at a
    /// lower priority to beat the menu bar's auto-focus on window activation.
    /// Stamping every request means a retry can tell that focus has since been
    /// claimed deliberately somewhere else and step aside instead of yanking it
    /// back after the user has already moved on.
    /// </summary>
    private int BeginFocusRequest() => ++m_focusRequestGeneration;

    /// <summary>
    /// True when nothing in this window holds keyboard focus, or when it is held
    /// by the top menu -- the only two states a deferred search-box focus retry
    /// is allowed to override.
    /// </summary>
    private bool IsFocusReclaimable()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused switch
        {
            null => true,
            Avalonia.Visual visual => visual.GetSelfAndVisualAncestors()
                .OfType<Menu>()
                .Any(menu => menu.Name == "TopMenu"),
            _ => false,
        };
    }

    private bool TryRedirectToSearchBox(string text, object? source)
    {
        // A modal overlay owns the keyboard while it is up. Without this the
        // encrypted-database password prompt - an in-window overlay, not a
        // separate window - let every character the user typed reach the search
        // box covered by it, so the password accumulated in cleartext in a
        // TwoWay-bound filter that feeds RecentSearches, and never reached the
        // password field at all. Every other handler in this file already has
        // this guard; this one was the exception. (bugs-opus F3)
        if (DataContext is MainWindowViewModel guardViewModel
            && (guardViewModel.IsSettingsOpen || guardViewModel.IsWelcomeOpen
                || guardViewModel.IsPasswordPromptOpen || guardViewModel.IsAiPromptOpen
                || guardViewModel.SessionLogs.IsOpen))
        {
            return false;
        }

        var searchBox = GetSearchBox();
        if (searchBox is null || searchBox.IsKeyboardFocusWithin)
        {
            return false;
        }

        // Don't redirect if already in a text input
        if (IsFromTextInput(source))
        {
            return false;
        }

        // Control characters reach TextInput for Tab, Enter, Escape, Backspace and
        // Ctrl+letter chords. None of those are filter text.
        foreach (var character in text)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        BeginFocusRequest();
        searchBox.Focus();
        if (DataContext is MainWindowViewModel vm)
        {
            var newText = (vm.SearchText ?? string.Empty) + text;
            vm.SearchText = newText;
            // Move the caret past the redirected text so that subsequent
            // keystrokes (which land natively once focus has transferred) are
            // appended in order rather than inserted at the start.
            searchBox.CaretIndex = newText.Length;
        }
        return true;
    }

    private void OnClipsListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || m_clipsListBox is null
            || viewModel.SelectedClip is null)
        {
            return;
        }

        ExecutePasteSelectedAndHide(viewModel);
        e.Handled = true;
    }

    private void OnClipsListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || e.Source is not Avalonia.Visual sourceVisual)
        {
            return;
        }

        var pointerProperties = e.GetCurrentPoint(this).Properties;
        var pointerKind = pointerProperties.PointerUpdateKind;
        if (pointerKind != PointerUpdateKind.LeftButtonPressed
            && pointerKind != PointerUpdateKind.RightButtonPressed)
        {
            return;
        }

        var clipElement = sourceVisual.GetSelfAndVisualAncestors()
            .OfType<StyledElement>()
            .FirstOrDefault(current => current.DataContext is ClipItemViewModel);
        if (clipElement?.DataContext is not ClipItemViewModel clip)
        {
            return;
        }

        // Avalonia's ListBox does not auto-select on right-click. Make the
        // right-clicked clip the selection so the shared ContextMenu (bound to
        // SelectedClip) targets it.
        if (pointerKind == PointerUpdateKind.RightButtonPressed)
        {
            viewModel.SelectedClip = clip;
            return;
        }

        // Stash the pointer position so PointerMoved can detect a drag
        // gesture once the pointer has travelled past the system threshold.
        // Cache the original PressedEventArgs — Avalonia's DoDragDropAsync
        // requires it as the trigger event.
        m_dragStartPoint = e.GetPosition(this);
        m_dragCandidateClip = clip;
        m_dragPressedArgs = e;

        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift);
        if ((modifiers & KeyModifiers.Shift) == KeyModifiers.Shift)
        {
            viewModel.ExtendClipCheckedSelection(clip, (modifiers & KeyModifiers.Control) == KeyModifiers.Control);
            e.Handled = true;
            return;
        }

        if ((modifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            viewModel.ToggleClipCheckedSelection(clip);
            e.Handled = true;
        }
    }

    private async void OnClipsListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (m_dragInProgress
            || m_dragStartPoint is not { } start
            || m_dragCandidateClip is null
            || m_dragPressedArgs is null
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            // Pointer released or button changed — abandon the drag candidate.
            m_dragStartPoint = null;
            m_dragCandidateClip = null;
            m_dragPressedArgs = null;
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - start.X) < DragThreshold && Math.Abs(current.Y - start.Y) < DragThreshold)
        {
            return;
        }

        // We're committed to a drag — block re-entry and the pointer-released
        // handler from re-running selection logic mid-drag.
        m_dragInProgress = true;
        var pressedArgs = m_dragPressedArgs;
        try
        {
            // If the user hasn't checked anything, ensure SelectedClip is the
            // row they pressed on so single-row drag uses the expected clip.
            if (!viewModel.HasCheckedClips)
            {
                viewModel.SelectedClip = m_dragCandidateClip;
            }

            var storageProvider = StorageProvider;
            if (storageProvider is null)
            {
                return;
            }

            var payload = await viewModel.BuildDragPayloadForCurrentSelectionAsync(storageProvider);
            if (payload is null)
            {
                return;
            }

            DragDropEffects effect;
            try
            {
                effect = await DragDrop.DoDragDropAsync(pressedArgs, payload, DragDropEffects.Copy | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Drag-out failed: {ex.Message}");
                return;
            }

            if (effect != DragDropEffects.None)
            {
                HidePopupAfterDrag(viewModel);
            }
        }
        finally
        {
            m_dragInProgress = false;
            m_dragStartPoint = null;
            m_dragCandidateClip = null;
            m_dragPressedArgs = null;
        }
    }

    private void OnClipsListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!m_dragInProgress)
        {
            m_dragStartPoint = null;
            m_dragCandidateClip = null;
            m_dragPressedArgs = null;
        }
    }

    private void HidePopupAfterDrag(MainWindowViewModel viewModel)
    {
        _ = viewModel.ClearSearchFilterAsync(forceRefresh: false);
        Hide();
    }

    private void OnPopupDragOver(object? sender, DragEventArgs e)
    {
        // Accept anything we know how to import — files, image bytes, or text.
        var data = e.DataTransfer;
        if (data is null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (data.Contains(DataFormat.File)
            || data.Contains(DataFormat.Text)
            || data.Contains(DataFormat.Bitmap))
        {
            e.DragEffects = DragDropEffects.Copy;
            return;
        }

        e.DragEffects = DragDropEffects.None;
    }

    private async void OnPopupDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || e.DataTransfer is null)
        {
            return;
        }

        // Resolve the source app (if any) the same way the clipboard monitor
        // does for normal captures — gives the imported clip a meaningful
        // "Source app" badge.
        ClipboardSourceApplicationInfo? sourceInfo = null;
        try
        {
            sourceInfo = (Application.Current as App)?.Services.GetService(typeof(ISourceApplicationResolver)) is ISourceApplicationResolver resolver
                ? resolver.TryResolve(includeIcon: false)
                : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Drag-in source resolve failed: {ex.Message}");
        }

        var imported = await viewModel.ImportDroppedDataAsync(e.DataTransfer, sourceInfo);
        if (imported > 0)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    private bool TryHandleClipRecopyShortcut(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (m_clipsListBox?.IsKeyboardFocusWithin != true || viewModel.SelectedClip is null)
        {
            return false;
        }

        var relevantModifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        if (e.Key != Key.C || relevantModifiers != KeyModifiers.Control)
        {
            return false;
        }

        ExecuteCopySelectedWithoutClosing(viewModel);
        return true;
    }

    private bool TryHandleClipPasteShortcut(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        var relevantModifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        if (e.Key != Key.V)
        {
            return false;
        }

        // Plain Ctrl+V from the clip list: paste the selected clip.
        var fromClipList = m_clipsListBox?.IsKeyboardFocusWithin == true;
        if (relevantModifiers == KeyModifiers.Control && fromClipList && viewModel.SelectedClip is not null)
        {
            ExecutePasteSelectedAndHide(viewModel);
            return true;
        }

        // Ctrl+Shift+V from anywhere (including the search box): paste the
        // currently selected clip, or the first clip if nothing is selected.
        // This lets the user filter and Ctrl+Shift+V to paste without leaving
        // the search box (where plain Ctrl+V is reserved for pasting into the
        // filter text).
        if (relevantModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            if (viewModel.IsSettingsOpen || viewModel.IsWelcomeOpen || viewModel.IsPasswordPromptOpen
                || viewModel.IsAiPromptOpen || viewModel.SessionLogs.IsOpen)
            {
                return false;
            }

            if (viewModel.SelectedClip is null)
            {
                if (viewModel.Clips.Count == 0)
                {
                    return false;
                }
                viewModel.SelectedClip = viewModel.GetDefaultAutoSelectedClip() ?? viewModel.Clips[0];
            }

            ExecutePasteSelectedAndHide(viewModel);
            return true;
        }

        return false;
    }

    private bool TryHandleEnterToCopyShortcut(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (viewModel.SelectedClip is null || viewModel.IsSettingsOpen || viewModel.IsWelcomeOpen || viewModel.SessionLogs.IsOpen)
        {
            return false;
        }

        // Block Enter in multi-line inputs. Allow it from single-line TextBoxes (e.g., the search box).
        if (IsKeyEventFromMultiLineEditor(e))
        {
            return false;
        }

        var relevantModifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        if (e.Key != Key.Enter || relevantModifiers != KeyModifiers.None)
        {
            return false;
        }

        ExecutePasteSelectedAndHide(viewModel);
        return true;
    }

    private static void ExecuteCopySelectedWithoutClosing(MainWindowViewModel viewModel)
    {
        viewModel.CopySelectedCommand.Execute().Subscribe();
    }

    private async void ExecutePasteSelectedAndHide(MainWindowViewModel viewModel)
    {
        // Step 1: Restore focus to the target window synchronously while we are still
        // inside the user-input event handler and guaranteed to hold the foreground lock.
        // AttachThreadInput ensures SetForegroundWindow succeeds regardless of lock state.
        m_systemInteractionService?.RestoreCapturedForeground();

        // Step 2: Copy to clipboard. Clipboard operations do not require the caller to be
        // the foreground window, so this can safely happen after we've yielded focus.
        var copied = await viewModel.TryCopySelectedForPasteAsync();
        if (!copied)
        {
            return;
        }

        // Step 3: Hide Clipthrough. Target has already been scheduled to receive focus.
        _ = viewModel.ClearSearchFilterAsync(forceRefresh: false);
        Hide();

        // Step 4: Give the OS time to process WM_ACTIVATE / WM_SETFOCUS on the target.
        await Task.Delay(150);

        // Step 5: Deliver Ctrl+V to the (now foreground) target window.
        m_systemInteractionService?.SimulatePasteKeystroke();
    }

    private static bool TryHandleEditedClipShortcut(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (!IsKeyEventFromTextInput(e))
        {
            return false;
        }

        var relevantModifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        if (e.Key != Key.Enter || relevantModifiers != KeyModifiers.Control || !viewModel.ShowCopyEditedClipButton)
        {
            return false;
        }

        viewModel.CopyEditedClipCommand.Execute().Subscribe();
        return true;
    }

    private async void OnEditableClipLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        await viewModel.CommitEditedClipOnFocusLossAsync();
    }

    private async void OnCopyEditedImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var imageEditor = this.FindControl<EmbeddedImageEditorView>("SelectedImageEditor");
        var editedBytes = imageEditor?.GetEditedImageBytes();
        if (editedBytes is not { Length: > 0 })
        {
            return;
        }

        await viewModel.CopyEditedImageAsync(editedBytes);
        e.Handled = true;
    }

    private void OnResetEditedImageClick(object? sender, RoutedEventArgs e)
    {
        var imageEditor = this.FindControl<EmbeddedImageEditorView>("SelectedImageEditor");
        imageEditor?.Reset();
        e.Handled = true;
    }

    private void OnTransformMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || sender is not MenuItem mi)
        {
            return;
        }
        if (mi.CommandParameter is not TextTransformation t || t == TextTransformation.None)
        {
            return;
        }
        viewModel.ApplyTextTransformationCommand.Execute(t).Subscribe();
        e.Handled = true;
    }

    private readonly System.Collections.Generic.List<MenuItem> m_aiRoots = new();
    private System.Collections.Specialized.INotifyCollectionChanged? m_aiSubscription;

    private void PopulateTransformMenus()
    {
        m_aiRoots.Clear();

        var editMenu = this.FindControl<MenuItem>("EditTransformMenu");
        if (editMenu is not null)
        {
            editMenu.Items.Clear();
            foreach (var control in BuildTransformMenuItems(includeAccessKeys: true))
            {
                editMenu.Items.Add(control);
            }
        }

        var flyout = this.FindControl<Button>("ToolbarTransformButton")?.Flyout as MenuFlyout;
        if (flyout is not null)
        {
            flyout.Items.Clear();
            foreach (var control in BuildTransformMenuItems(includeAccessKeys: false))
            {
                flyout.Items.Add(control);
            }
        }

        RefreshDynamicSubmenus();
    }

    private System.Collections.Generic.IEnumerable<Control> BuildTransformMenuItems(bool includeAccessKeys)
    {
        var vm = DataContext as MainWindowViewModel;
        var controls = new System.Collections.Generic.List<Control>();
        var showTextTransforms = vm?.HasTextTransformTarget ?? false;
        var showAi = vm?.IsAiMenuVisible == true;

        if (showTextTransforms)
        {
            controls.AddRange(TransformMenuCatalog.BuildItems(OnTransformMenuClick));
        }

        if (showAi)
        {
            var aiRoot = new MenuItem { Header = includeAccessKeys ? "_AI" : "AI" };
            if (includeAccessKeys)
            {
                aiRoot.InputGesture = s_aiTransformGesture;
            }
            m_aiRoots.Add(aiRoot);
            controls.Add(aiRoot);
        }

        while (controls.Count > 0 && controls[^1] is Separator)
        {
            controls.RemoveAt(controls.Count - 1);
        }

        return controls;
    }

    private void RefreshDynamicSubmenus()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (m_aiSubscription is not null)
        {
            m_aiSubscription.CollectionChanged -= OnAiEntriesChanged;
        }

        m_aiSubscription = vm.AiMenuEntries as System.Collections.Specialized.INotifyCollectionChanged;
        if (m_aiSubscription is not null)
        {
            m_aiSubscription.CollectionChanged += OnAiEntriesChanged;
        }

        RebuildAiSubmenus(vm);
    }

    private void OnAiEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel)
        {
            Dispatcher.UIThread.Post(PopulateTransformMenus);
        }
    }

    private void RebuildAiSubmenus(MainWindowViewModel vm)
    {
        foreach (var root in m_aiRoots)
        {
            root.Items.Clear();
            foreach (var entry in vm.VisibleAiMenuEntries)
            {
                var child = new MenuItem
                {
                    Header = entry.Label,
                    Command = vm.InvokeAiMenuEntryCommand,
                    CommandParameter = entry,
                };
                root.Items.Add(child);
            }
        }
    }

    private bool TryHandleClipIndexShortcut(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (viewModel.IsSettingsOpen || viewModel.IsWelcomeOpen || viewModel.SessionLogs.IsOpen)
        {
            return false;
        }

        // Don't intercept when inside a text input
        if (IsKeyEventFromTextInput(e))
        {
            return false;
        }

        var key = e.Key;
        int index = key switch
        {
            Key.D1 => 1,
            Key.D2 => 2,
            Key.D3 => 3,
            Key.D4 => 4,
            Key.D5 => 5,
            Key.D6 => 6,
            Key.D7 => 7,
            Key.D8 => 8,
            Key.D9 => 9,
            _ => 0,
        };

        if (index == 0)
        {
            return false;
        }

        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);

        if (modifiers == KeyModifiers.Control)
        {
            // Select first, then run the same sequence Enter uses: restore the target's
            // foreground while we still hold the input lock, copy, hide, then send Ctrl+V.
            // Copying without pasting would leave the user to press Ctrl+V themselves,
            // which is the whole thing the shortcut is meant to save.
            if (!viewModel.SelectClipByIndex(index))
            {
                return false;
            }

            ExecutePasteSelectedAndHide(viewModel);
            return true;
        }

        // Alt+digit doesn't conflict with menu access keys (which use Alt+letter).
        if (modifiers == KeyModifiers.Alt)
        {
            viewModel.SelectClipByIndex(index);
            return true;
        }

        return false;
    }

    private void OnTopMenuGotFocus(object? sender, RoutedEventArgs e)
    {
        // The top-level menu sometimes grabs focus when the window activates
        // (Win32 menu accelerator handling kicks in even without an Alt press).
        // If no submenu is actually open, the user did not intend to interact
        // with the menu — bounce focus back to the search box.
        if (sender is not Menu menu)
        {
            return;
        }

        var anySubmenuOpen = false;
        foreach (var item in menu.Items)
        {
            if (item is MenuItem mi && mi.IsSubMenuOpen)
            {
                anySubmenuOpen = true;
                break;
            }
        }

        if (anySubmenuOpen)
        {
            return;
        }

        FocusInitialPopupTarget();
    }

    private async void OnSearchBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetSearchBoxFocused(true);
            await viewModel.LoadRecentSearchesAsync();
        }
    }

    private async void OnSearchBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        await Task.Delay(150);
        if (DataContext is MainWindowViewModel viewModel
            && GetSearchBox()?.IsKeyboardFocusWithin != true
            && this.FindControl<ListBox>("SearchSuggestionsList")?.IsKeyboardFocusWithin != true)
        {
            viewModel.SetSearchBoxFocused(false);
        }
    }

    /// <summary>
    /// Applies a suggestion on click. Deliberately <c>Tapped</c> rather than
    /// <c>SelectionChanged</c>, for two independent reasons.
    /// </summary>
    /// <remarks>
    /// Correctness: applying rewrites <c>SearchText</c>, which clears and refills
    /// <c>FilteredRecentSearches</c> - the <c>ItemsSource</c> of this very list.
    /// <c>SelectionChanged</c> is raised from inside the selection model's commit, so
    /// mutating the collection there left the model indexing a list it had already
    /// measured, and it threw <c>ArgumentOutOfRangeException</c> out of
    /// <c>SelectedItems.GetEnumerator</c>. <c>Tapped</c> is raised from the pointer
    /// release, after that commit has finished, so the hazard does not exist rather
    /// than being timed around.
    ///
    /// Behaviour: <c>SelectionChanged</c> also fires while arrowing through the list,
    /// which would apply every entry the highlight passed over and makes keyboard
    /// navigation impossible. Moving the highlight and choosing an entry have to be
    /// separate events.
    /// </remarks>
    private void OnSearchSuggestionTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            ApplySearchSuggestion(listBox, listBox.SelectedItem as string);
        }
    }

    private void ApplySearchSuggestion(ListBox listBox, string? suggestion)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || string.IsNullOrWhiteSpace(suggestion))
        {
            return;
        }

        viewModel.ApplySearchSuggestion(suggestion);

        // Clear the selection so choosing the same entry again raises a fresh event.
        listBox.SelectedItem = null;
        FocusSearchBox();
    }

    private ListBox? GetSearchSuggestionsList() => this.FindControl<ListBox>("SearchSuggestionsList");

    /// <summary>
    /// Moves focus onto a suggestion, returning whether it landed.
    /// </summary>
    /// <remarks>
    /// Focuses the container rather than the list. An Avalonia <see cref="ListBox"/> is
    /// not itself focusable, so <c>listBox.Focus()</c> returns without doing anything and
    /// the keyboard route into the dropdown silently fails to exist - the same reason
    /// <see cref="FocusSelectedClipInList"/> reaches for the <see cref="ListBoxItem"/>.
    /// </remarks>
    private bool FocusSearchSuggestion(int index)
    {
        var suggestions = GetSearchSuggestionsList();
        if (suggestions is null || index < 0 || index >= suggestions.ItemCount)
        {
            return false;
        }

        suggestions.SelectedIndex = index;
        suggestions.ScrollIntoView(index);

        return suggestions.ContainerFromIndex(index) is ListBoxItem item && item.Focus();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (m_subscribedSessionLogs is not null)
        {
            m_subscribedSessionLogs.PropertyChanged -= OnSessionLogsPropertyChanged;
            m_subscribedSessionLogs = null;
        }

        if (m_subscribedViewModel is not null)
        {
            m_subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            m_subscribedViewModel.HelpRequested -= OnHelpRequested;
            m_subscribedViewModel.AboutRequested -= OnAboutRequested;
            m_subscribedViewModel.Clips.CollectionChanged -= OnClipsCollectionChanged;
            m_subscribedViewModel = null;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            m_subscribedSessionLogs = viewModel.SessionLogs;
            m_subscribedSessionLogs.PropertyChanged += OnSessionLogsPropertyChanged;
            UpdateSessionLogsWindowVisibility(m_subscribedSessionLogs.IsOpen);

            m_subscribedViewModel = viewModel;
            m_subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            m_subscribedViewModel.HelpRequested += OnHelpRequested;
            m_subscribedViewModel.AboutRequested += OnAboutRequested;
            m_subscribedViewModel.Clips.CollectionChanged += OnClipsCollectionChanged;
            UpdateSettingsWindowVisibility(viewModel.IsSettingsOpen);
            UpdateAiPromptWindowVisibility(viewModel.IsAiPromptOpen);
            // The encrypted-database prompt is already open by the time the view
            // binds, so the PropertyChanged path above never fires for it.
            UpdatePasswordPromptFocus(viewModel.IsPasswordPromptOpen);
            PopulateTransformMenus();
        }
    }

    /// <summary>
    /// A refresh rebuilds the view model of any row whose clip changed, and
    /// background enrichment (OCR text, the sensitivity scan, source icons)
    /// makes that happen seconds after a capture, while the user is arrowing
    /// through the list. Replacing the row destroys its container, and Avalonia
    /// then leaves <em>nothing</em> focused: the arrow keys stop working
    /// entirely until the user clicks or tabs back in.
    ///
    /// Measured, a null focused element is the exact signature of that and of
    /// nothing else here. Moving focus deliberately always lands somewhere, and
    /// this window keeps its focused element even while another window is
    /// active — so no separate "was the list focused" flag is needed, and
    /// checking for null is what keeps a background refresh from stealing the
    /// keyboard away from the search box.
    /// </summary>
    private void OnClipsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            return;
        }

        // The check has to be deferred, not just the restore. The ListBox
        // subscribes to this same collection after this window does, so its
        // handler -- the one that destroys the container -- has not run yet and
        // focus still looks fine from here. Containers are realised during
        // layout too, so the replacement row does not exist yet either.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (m_clipsListBox is null
                    || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not null)
                {
                    return;
                }

                // Containers are created during layout, and a posted job can run
                // before the one triggered by this collection change. Force the
                // pass so the row exists, then focus it here rather than posting
                // again: the restore is only correct while focus is still null,
                // and another hop gives that window a chance to change.
                m_clipsListBox.UpdateLayout();

                // An emptied list has no row to return to; the search box is the
                // only place the user can act from, and leaving focus nowhere
                // would strand the keyboard just the same.
                if (m_clipsListBox.SelectedIndex < 0)
                {
                    FocusSearchBox();
                    return;
                }

                m_clipsListBox.ScrollIntoView(m_clipsListBox.SelectedIndex);
                if (m_clipsListBox.ContainerFromIndex(m_clipsListBox.SelectedIndex) is ListBoxItem row)
                {
                    BeginFocusRequest();
                    row.Focus();
                }
            },
            DispatcherPriority.Loaded);
    }

    private void OnHelpRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var window = new HelpWindow();
            window.Show(this);
        });
    }

    private void OnAboutRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var window = new AboutWindow
            {
                DataContext = sender as MainWindowViewModel ?? DataContext,
            };
            window.Show(this);
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen) && sender is MainWindowViewModel vm)
        {
            Dispatcher.UIThread.Post(() => UpdateSettingsWindowVisibility(vm.IsSettingsOpen));
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsAiPromptOpen) && sender is MainWindowViewModel aiVm)
        {
            Dispatcher.UIThread.Post(() => UpdateAiPromptWindowVisibility(aiVm.IsAiPromptOpen));
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsPasswordPromptOpen) && sender is MainWindowViewModel passwordVm)
        {
            UpdatePasswordPromptFocus(passwordVm.IsPasswordPromptOpen);
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.SelectedClip) && m_focusClipOnNextSelectionChange)
        {
            m_focusClipOnNextSelectionChange = false;
            Dispatcher.UIThread.Post(FocusInitialPopupTarget, DispatcherPriority.Background);
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.HasTextTransformTarget)
            || e.PropertyName == nameof(MainWindowViewModel.HasImageTransformTarget)
            || e.PropertyName == nameof(MainWindowViewModel.HasTransformableTarget)
            || e.PropertyName == nameof(MainWindowViewModel.IsAiMenuVisible))
        {
            Dispatcher.UIThread.Post(PopulateTransformMenus);
        }
    }

    private void UpdatePasswordPromptFocus(bool open)
    {
        if (!open)
        {
            return;
        }

        // Nothing focused the password field before, because both focus entry
        // points bail out while the prompt is up. That left the first keystroke
        // with no home, which is what let TryRedirectToSearchBox claim it.
        // Guarding the redirect alone would have made the prompt silently eat
        // the password instead of misfiling it - still broken, just quieter.
        var passwordBox = this.FindControl<TextBox>("PasswordPromptTextBox");
        if (passwordBox is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel viewModel && viewModel.IsPasswordPromptOpen)
            {
                passwordBox.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void UpdateSettingsWindowVisibility(bool open)
    {
        if (open)
        {
            if (m_settingsWindow is null)
            {
                m_settingsWindow = new SettingsWindow
                {
                    DataContext = m_subscribedViewModel,
                };
                m_settingsWindow.Closing += OnSettingsWindowClosing;
                m_settingsWindow.Show(this);
            }
            else
            {
                ShowOwnedWindow(m_settingsWindow);
            }
        }
        else if (m_settingsWindow is not null)
        {
            var window = m_settingsWindow;
            m_settingsWindow = null;
            window.Closing -= OnSettingsWindowClosing;
            CloseChildWindow(window, "settings");
        }
    }

    private void OnSettingsWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is Window window)
        {
            window.Closing -= OnSettingsWindowClosing;
        }

        m_settingsWindow = null;

        if (m_subscribedViewModel is not null && m_subscribedViewModel.IsSettingsOpen)
        {
            m_subscribedViewModel.CloseSettingsCommand.Execute().Subscribe();
        }
    }

    private void UpdateAiPromptWindowVisibility(bool open)
    {
        if (open)
        {
            if (m_aiPromptWindow is null)
            {
                m_aiPromptWindow = new AiPromptWindow
                {
                    DataContext = m_subscribedViewModel,
                };
                m_aiPromptWindow.Closing += OnAiPromptWindowClosing;
                m_aiPromptWindow.Show(this);
            }
            else
            {
                ShowOwnedWindow(m_aiPromptWindow);
            }
        }
        else if (m_aiPromptWindow is not null)
        {
            var window = m_aiPromptWindow;
            m_aiPromptWindow = null;
            window.Closing -= OnAiPromptWindowClosing;
            CloseChildWindow(window, "AI prompt");
            FocusInitialPopupTarget();
        }
    }

    private void OnAiPromptWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is Window window)
        {
            window.Closing -= OnAiPromptWindowClosing;
        }

        m_aiPromptWindow = null;

        if (m_subscribedViewModel is not null && m_subscribedViewModel.IsAiPromptOpen)
        {
            m_subscribedViewModel.CancelAiPromptCommand.Execute().Subscribe();
        }

        FocusInitialPopupTarget();
    }

    private void OnSessionLogsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionLogsViewModel.IsOpen) && sender is SessionLogsViewModel vm)
        {
            Dispatcher.UIThread.Post(() => UpdateSessionLogsWindowVisibility(vm.IsOpen));
        }
    }

    private void UpdateSessionLogsWindowVisibility(bool open)
    {
        if (open)
        {
            if (m_sessionLogsWindow is null)
            {
                m_sessionLogsWindow = new SessionLogsWindow
                {
                    DataContext = m_subscribedSessionLogs
                };
                m_sessionLogsWindow.Closing += OnSessionLogsWindowClosing;
                m_sessionLogsWindow.Show(this);
            }
            else
            {
                ShowOwnedWindow(m_sessionLogsWindow);
            }
        }
        else if (m_sessionLogsWindow is not null)
        {
            var window = m_sessionLogsWindow;
            m_sessionLogsWindow = null;
            window.Closing -= OnSessionLogsWindowClosing;
            CloseChildWindow(window, "session logs");
        }
    }

    private void OnSessionLogsWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // User closed via native chrome → sync VM state.
        if (sender is Window window)
        {
            window.Closing -= OnSessionLogsWindowClosing;
        }

        m_sessionLogsWindow = null;

        if (m_subscribedSessionLogs is not null && m_subscribedSessionLogs.IsOpen)
        {
            m_subscribedSessionLogs.Close();
        }
    }

    public void FocusSearchBox()
    {
        if (DataContext is MainWindowViewModel viewModel
            && (viewModel.IsSettingsOpen || viewModel.IsWelcomeOpen || viewModel.IsPasswordPromptOpen
                || viewModel.IsAiPromptOpen || viewModel.SessionLogs.IsOpen))
        {
            return;
        }

        var searchBox = GetSearchBox();
        if (searchBox is null)
        {
            return;
        }

        // Avalonia + Win32 will sometimes hand focus to the menu bar when the
        // window is activated. Schedule the focus call at three priorities so
        // we win regardless of when the menu's auto-focus runs.
        var generation = BeginFocusRequest();

        void TryFocus(bool isRetry)
        {
            // A newer focus request supersedes this one entirely.
            if (m_focusRequestGeneration != generation || searchBox.IsKeyboardFocusWithin)
            {
                return;
            }

            // The first attempt is the caller's explicit intent and always wins.
            // The retries exist only to beat the menu bar, so they must stand
            // down once focus has landed on a real control -- a clip, a button,
            // the sort box -- rather than dragging it back a frame later.
            if (isRetry && !IsFocusReclaimable())
            {
                return;
            }

            searchBox.Focus();
        }

        Dispatcher.UIThread.Post(() =>
        {
            TryFocus(isRetry: false);
            Dispatcher.UIThread.Post(() => TryFocus(isRetry: true), DispatcherPriority.Input);
            Dispatcher.UIThread.Post(() => TryFocus(isRetry: true), DispatcherPriority.Background);
        }, DispatcherPriority.Input);
    }

    public void FocusClipOnNextActivation()
    {
        m_focusClipOnNextActivation = true;
        m_focusClipOnNextSelectionChange = true;
        if (IsActive)
        {
            m_focusClipOnNextActivation = false;
            FocusInitialPopupTarget();
        }
    }

    private void FocusInitialPopupTarget()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            FocusSearchBox();
            return;
        }

        if (viewModel.IsSettingsOpen || viewModel.IsWelcomeOpen || viewModel.IsPasswordPromptOpen
            || viewModel.IsAiPromptOpen || viewModel.SessionLogs.IsOpen)
        {
            return;
        }

        if (viewModel.SelectedClip is null)
        {
            viewModel.SelectedClip = viewModel.GetDefaultAutoSelectedClip();
        }

        if (viewModel.SelectedClip is null)
        {
            FocusSearchBox();
            return;
        }

        FocusSelectedClipInList();
    }

    private TextBox? GetSearchBox() => this.FindControl<TextBox>("SearchTextBox");

    /// <summary>
    /// Test seam for the deferred clip-list focus path, which is otherwise only
    /// reachable through key handling.
    /// </summary>
    internal void FocusSelectedClipForTests() => FocusSelectedClipInList();

    /// <summary>
    /// True when the key event is a plain printable keystroke (letters,
    /// digits, space, punctuation, navigation keys that text inputs handle
    /// natively) with no Ctrl/Alt/Meta modifier — i.e. something that
    /// definitely belongs to the focused text control and not to any of our
    /// shortcut helpers. Shift alone is allowed (capitalisation).
    /// </summary>
    private static bool IsPlainPrintableShortcut(KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);
        if (modifiers != KeyModifiers.None)
        {
            return false;
        }

        // Enter, Escape, Tab, F-keys etc. drive global behaviour even from
        // inside a text input, so the shortcut chain still needs to see them.
        return e.Key
            is (>= Key.A and <= Key.Z)
            or (>= Key.D0 and <= Key.D9)
            or (>= Key.NumPad0 and <= Key.NumPad9)
            or Key.Space
            or Key.OemPeriod
            or Key.OemComma
            or Key.OemMinus
            or Key.OemPlus
            or Key.OemQuestion
            or Key.OemQuotes
            or Key.OemSemicolon
            or Key.OemOpenBrackets
            or Key.OemCloseBrackets
            or Key.OemPipe
            or Key.OemTilde
            or Key.OemBackslash
            or Key.Back
            or Key.Delete
            or Key.Left
            or Key.Right;
    }

    /// <summary>
    /// Returns true when the key event originated from a text input control or
    /// any of its inner descendants. AvaloniaEdit's <see cref="AvaloniaEdit.TextEditor"/>
    /// routes key events from its inner TextArea, so a direct <c>e.Source is TextEditor</c>
    /// check misses real keystrokes typed into the editor. Walk the visual ancestry so
    /// keystrokes from inside any TextBox / TextEditor / SyntaxTextEditor are recognised.
    /// </summary>
    private static bool IsKeyEventFromTextInput(KeyEventArgs e) => IsFromTextInput(e.Source);

    private static bool IsFromTextInput(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        if (visual is TextBox or AvaloniaEdit.TextEditor or SyntaxTextEditor)
        {
            return true;
        }

        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (ancestor is TextBox or AvaloniaEdit.TextEditor or SyntaxTextEditor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Same as <see cref="IsKeyEventFromTextInput"/> but limited to multi-line
    /// editors (AvaloniaEdit's <see cref="AvaloniaEdit.TextEditor"/> and
    /// <see cref="SyntaxTextEditor"/>, plus any TextBox with AcceptsReturn=true).
    /// Used by handlers that should still process Escape / Enter from the
    /// single-line search box.
    /// </summary>
    private static bool IsKeyEventFromMultiLineEditor(KeyEventArgs e)
    {
        if (e.Source is not Visual source)
        {
            return false;
        }

        if (source is AvaloniaEdit.TextEditor or SyntaxTextEditor or TextBox { AcceptsReturn: true })
        {
            return true;
        }

        foreach (var ancestor in source.GetVisualAncestors())
        {
            if (ancestor is AvaloniaEdit.TextEditor or SyntaxTextEditor or TextBox { AcceptsReturn: true })
            {
                return true;
            }
        }

        return false;
    }

    private void FocusSortBox()
    {
        var sortBox = this.FindControl<ComboBox>("SortComboBox");
        if (sortBox is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => sortBox.Focus(), DispatcherPriority.Input);
    }

    private static int? TryGetShortcutNumber(Key key) => key switch
    {
        Key.D1 or Key.NumPad1 => 1,
        Key.D2 or Key.NumPad2 => 2,
        Key.D3 or Key.NumPad3 => 3,
        Key.D4 or Key.NumPad4 => 4,
        Key.D5 or Key.NumPad5 => 5,
        _ => null,
    };

    private void ShowOwnedWindow(Window window)
    {
        if (!IsVisible)
        {
            Show();

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }

        if (!window.IsVisible)
        {
            window.Show(this);
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    internal void RestoreOwnedWindowsForCurrentState()
    {
        if (m_subscribedViewModel?.IsSettingsOpen == true)
        {
            UpdateSettingsWindowVisibility(true);
        }

        if (m_subscribedSessionLogs?.IsOpen == true)
        {
            UpdateSessionLogsWindowVisibility(true);
        }

        if (m_subscribedViewModel?.IsAiPromptOpen == true)
        {
            UpdateAiPromptWindowVisibility(true);
        }
    }

}
