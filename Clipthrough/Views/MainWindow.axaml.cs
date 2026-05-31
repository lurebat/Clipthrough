using System;
using System.ComponentModel;
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
    private Point? m_dragStartPoint;
    private ClipItemViewModel? m_dragCandidateClip;
    private PointerPressedEventArgs? m_dragPressedArgs;
    private bool m_dragInProgress;
    private const double DragThreshold = 4.0;

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
            try { m_sessionLogsWindow.Close(); } catch { }
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
            try { m_settingsWindow.Close(); } catch { }
            m_settingsWindow = null;
        }

        if (m_aiPromptWindow is not null)
        {
            m_aiPromptWindow.Closing -= OnAiPromptWindowClosing;
            try { m_aiPromptWindow.Close(); } catch { }
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
            return;
        }

        // Type-to-filter: redirect printable keystrokes to search box
        if (TryRedirectToSearchBox(e))
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

        // Don't interfere with arrow keys in multi-line text editors
        if (IsKeyEventFromTextInput(e))
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

        if (isSearchFocused && modifiers == KeyModifiers.Alt && (e.Key == Key.Down || e.Key == Key.Up))
        {
            _ = viewModel.NavigateSearchHistoryAsync(e.Key == Key.Up ? -1 : 1);
            return true;
        }

        // Plain Up from the search box also walks back through recent searches
        // (Down keeps moving focus into the clip list to preserve discoverability).
        if (isSearchFocused && modifiers == KeyModifiers.None && e.Key == Key.Up)
        {
            _ = viewModel.NavigateSearchHistoryAsync(-1);
            return true;
        }

        if (e.Key == Key.Tab && modifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))
        {
            viewModel.CycleSelectedViewerMode((modifiers & KeyModifiers.Shift) == KeyModifiers.Shift);
            return true;
        }

        // Tab from search box: move focus to clip list
        if (e.Key == Key.Tab && modifiers == KeyModifiers.None && isSearchFocused && viewModel.Clips.Count > 0)
        {
            if (viewModel.SelectedClip is null)
            {
                viewModel.SelectedClip = viewModel.GetDefaultAutoSelectedClip();
            }
            FocusSelectedClipInList();
            return true;
        }

        // Shift+Tab from clip list: return focus to search box
        if (e.Key == Key.Tab && modifiers == KeyModifiers.Shift && m_clipsListBox?.IsKeyboardFocusWithin == true)
        {
            FocusSearchBox();
            return true;
        }

        if (viewModel.Clips.Count == 0)
        {
            return false;
        }

        // Arrow Down from search box: move focus to clip list, select first clip if none selected
        if (e.Key == Key.Down && modifiers == KeyModifiers.None && isSearchFocused)
        {
            if (viewModel.SelectedClip is null)
            {
                viewModel.SelectedClip = viewModel.GetDefaultAutoSelectedClip();
            }
            FocusSelectedClipInList();
            return true;
        }

        // Home/End: jump to first/last clip (from search box or clip list)
        if (modifiers == KeyModifiers.None && (isSearchFocused || m_clipsListBox?.IsKeyboardFocusWithin == true))
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
            if (currentIndex == 0)
            {
                // Up from first clip: return focus to search box
                FocusSearchBox();
                return true;
            }

            var prevIndex = currentIndex - 1;
            if (prevIndex >= 0)
            {
                viewModel.SelectedClip = viewModel.Clips[prevIndex];
                FocusSelectedClipInList();
            }
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

        // Scroll the selected item into view
        if (m_clipsListBox.SelectedItem is not null)
        {
            m_clipsListBox.ScrollIntoView(m_clipsListBox.SelectedItem);
        }

        // Focus the ListBoxItem for the selected clip
        Dispatcher.UIThread.Post(() =>
        {
            if (m_clipsListBox.SelectedIndex < 0)
            {
                return;
            }

            var container = m_clipsListBox.ContainerFromIndex(m_clipsListBox.SelectedIndex);
            if (container is ListBoxItem item)
            {
                item.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private bool TryRedirectToSearchBox(KeyEventArgs e)
    {
        var searchBox = GetSearchBox();
        if (searchBox is null || searchBox.IsKeyboardFocusWithin)
        {
            return false;
        }

        // Don't redirect if already in a text input
        if (IsKeyEventFromTextInput(e))
        {
            return false;
        }

        // Only redirect unmodified or shift-modified printable keys
        var relevantModifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);
        if (relevantModifiers != KeyModifiers.None)
        {
            return false;
        }

        // Translate the key into a printable character (letters/digits only; shift handled naturally)
        char? typed = null;
        if (e.Key >= Key.A && e.Key <= Key.Z)
        {
            var letter = (char)('a' + (e.Key - Key.A));
            typed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift ? char.ToUpperInvariant(letter) : letter;
        }
        else if (e.Key >= Key.D0 && e.Key <= Key.D9 && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            typed = (char)('0' + (e.Key - Key.D0));
        }
        else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
        {
            typed = (char)('0' + (e.Key - Key.NumPad0));
        }
        else if (e.Key == Key.Space)
        {
            typed = ' ';
        }

        if (typed is null)
        {
            return false;
        }

        searchBox.Focus();
        if (DataContext is MainWindowViewModel vm)
        {
            var newText = (vm.SearchText ?? string.Empty) + typed.Value;
            vm.SearchText = newText;
            // Move the caret past the redirected character so that subsequent
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

    private static readonly (string Group, string Header, TextTransformation Kind)[] s_transformMenuEntries =
    {
        ("Case", "UPPERCASE", TextTransformation.UpperCase),
        ("Case", "lowercase", TextTransformation.LowerCase),
        ("Case", "Title Case", TextTransformation.TitleCase),
        ("Case", "Sentence case", TextTransformation.SentenceCase),
        ("Case", "UpperCamelCase", TextTransformation.UpperCamelCase),
        ("Case", "lowerCamelCase", TextTransformation.LowerCamelCase),
        ("Case", "From camelCase", TextTransformation.FromCamelCase),
        ("Whitespace", "Trim whitespace", TextTransformation.TrimWhitespace),
        ("Whitespace", "Collapse whitespace", TextTransformation.CollapseWhitespace),
        ("Whitespace", "Tabs → Spaces", TextTransformation.TabsToSpaces),
        ("Whitespace", "Spaces → Tabs", TextTransformation.SpacesToTabs),
        ("Lines", "Normalize line endings", TextTransformation.NormalizeEol),
        ("Lines", "Sort lines", TextTransformation.SortLines),
        ("Lines", "Reverse lines", TextTransformation.ReverseLines),
        ("Lines", "Remove empty lines", TextTransformation.RemoveEmptyLines),
        ("Lines", "Remove duplicate lines", TextTransformation.RemoveDuplicateLines),
        ("JSON", "JSON quote", TextTransformation.JsonQuote),
        ("JSON", "JSON unquote", TextTransformation.JsonUnquote),
        ("JSON", "JSON minify", TextTransformation.JsonMinify),
        ("JSON", "JSON pretty", TextTransformation.JsonPretty),
        ("JSON", "Lines → JSON array", TextTransformation.LinesToJsonArray),
        ("Encoding", "URL encode", TextTransformation.UrlEncode),
        ("Encoding", "URL decode", TextTransformation.UrlDecode),
        ("Encoding", "Base64 encode", TextTransformation.Base64Encode),
        ("Encoding", "Base64 decode", TextTransformation.Base64Decode),
        ("Cleanup", "Clean terminal formatting", TextTransformation.CleanTerminalFormatting),
        ("Convert", "Text table → HTML", TextTransformation.BoxTableToHtml),
    };

    private readonly System.Collections.Generic.List<MenuItem> m_scriptsRoots = new();
    private readonly System.Collections.Generic.List<MenuItem> m_aiRoots = new();
    private System.Collections.Specialized.INotifyCollectionChanged? m_scriptsSubscription;
    private System.Collections.Specialized.INotifyCollectionChanged? m_aiSubscription;

    private void PopulateTransformMenus()
    {
        m_scriptsRoots.Clear();
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
        var showScripts = vm?.VisibleUserScripts.Count > 0;
        var showAi = vm?.IsAiMenuVisible == true;

        if (showTextTransforms)
        {
            foreach (var grouping in s_transformMenuEntries.GroupBy(e => e.Group))
            {
                var entries = grouping.ToList();
                if (entries.Count == 1)
                {
                    var (_, header, kind) = entries[0];
                    var item = new MenuItem
                    {
                        Header = header,
                        CommandParameter = kind,
                    };
                    item.Click += OnTransformMenuClick;
                    controls.Add(item);
                    continue;
                }

                var groupRoot = new MenuItem { Header = grouping.Key };
                foreach (var (_, header, kind) in entries)
                {
                    var item = new MenuItem
                    {
                        Header = header,
                        CommandParameter = kind,
                    };
                    item.Click += OnTransformMenuClick;
                    groupRoot.Items.Add(item);
                }
                controls.Add(groupRoot);
            }
        }

        if (showScripts)
        {
            var scriptsRoot = new MenuItem { Header = includeAccessKeys ? "_Scripts" : "Scripts" };
            m_scriptsRoots.Add(scriptsRoot);
            controls.Add(scriptsRoot);
        }

        if (showAi)
        {
            var aiRoot = new MenuItem { Header = includeAccessKeys ? "_AI" : "AI" };
            if (includeAccessKeys)
            {
                try { aiRoot.InputGesture = KeyGesture.Parse("Ctrl+I"); } catch { }
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

        if (m_scriptsSubscription is not null)
        {
            m_scriptsSubscription.CollectionChanged -= OnUserScriptsChanged;
        }
        if (m_aiSubscription is not null)
        {
            m_aiSubscription.CollectionChanged -= OnAiEntriesChanged;
        }

        m_scriptsSubscription = vm.UserScripts as System.Collections.Specialized.INotifyCollectionChanged;
        if (m_scriptsSubscription is not null)
        {
            m_scriptsSubscription.CollectionChanged += OnUserScriptsChanged;
        }

        m_aiSubscription = vm.AiMenuEntries as System.Collections.Specialized.INotifyCollectionChanged;
        if (m_aiSubscription is not null)
        {
            m_aiSubscription.CollectionChanged += OnAiEntriesChanged;
        }

        RebuildScriptsSubmenus(vm);
        RebuildAiSubmenus(vm);
    }

    private void OnUserScriptsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel)
        {
            Dispatcher.UIThread.Post(PopulateTransformMenus);
        }
    }

    private void OnAiEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel)
        {
            Dispatcher.UIThread.Post(PopulateTransformMenus);
        }
    }

    private void RebuildScriptsSubmenus(MainWindowViewModel vm)
    {
        foreach (var root in m_scriptsRoots)
        {
            root.Items.Clear();
            foreach (var script in vm.VisibleUserScripts)
            {
                var child = new MenuItem
                {
                    Header = script.Name,
                    Command = vm.ApplyUserScriptCommand,
                    CommandParameter = script,
                };
                root.Items.Add(child);
            }
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

    private void OnScriptMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || sender is not MenuItem mi)
        {
            return;
        }
        if (mi.CommandParameter is not UserScript script)
        {
            return;
        }
        viewModel.ApplyUserScriptCommand.Execute(script).Subscribe();
        e.Handled = true;
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
            _ = viewModel.CopyClipByIndexAsync(index).ContinueWith(
                _ => Dispatcher.UIThread.Post(() => MinimizeWindow()),
                System.Threading.Tasks.TaskScheduler.Default);
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

    private void MinimizeWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                _ = viewModel.ClearSearchFilterAsync(forceRefresh: false);
            }
            Hide();
        });
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

    private void OnSearchSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        // Read the suggestion from the event's AddedItems instead of the
        // ListBox's SelectedItem. Refreshing FilteredRecentSearches (via
        // Clear/Add) can briefly null the selection and fire this handler
        // with stale state; AddedItems is empty during such churn, so the
        // guard below skips it cleanly.
        if (DataContext is not MainWindowViewModel viewModel
            || sender is not ListBox listBox)
        {
            return;
        }

        var suggestion = e.AddedItems?.OfType<string>().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(suggestion))
        {
            return;
        }

        viewModel.ApplySearchSuggestion(suggestion);
        listBox.SelectedItem = null;
        FocusSearchBox();
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
            UpdateSettingsWindowVisibility(viewModel.IsSettingsOpen);
            UpdateAiPromptWindowVisibility(viewModel.IsAiPromptOpen);
            PopulateTransformMenus();
        }
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
            try { window.Close(); } catch { }
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
            try { window.Close(); } catch { }
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
            try { window.Close(); } catch { }
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
        void TryFocus()
        {
            if (!searchBox.IsKeyboardFocusWithin)
            {
                searchBox.Focus();
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            TryFocus();
            Dispatcher.UIThread.Post(TryFocus, DispatcherPriority.Input);
            Dispatcher.UIThread.Post(TryFocus, DispatcherPriority.Background);
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
    private static bool IsKeyEventFromTextInput(KeyEventArgs e)
    {
        if (e.Source is not Visual source)
        {
            return false;
        }

        if (source is TextBox or AvaloniaEdit.TextEditor or SyntaxTextEditor)
        {
            return true;
        }

        foreach (var ancestor in source.GetVisualAncestors())
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
