using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Controls;
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

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (m_clipListScrollViewer is not null)
        {
            FocusSearchBox();
            return;
        }

        m_clipsListBox = this.FindControl<ListBox>("ClipsListBox");
        if (m_clipsListBox is not null)
        {
            m_clipsListBox.AddHandler(InputElement.DoubleTappedEvent, OnClipsListDoubleTapped, RoutingStrategies.Bubble);
            m_clipsListBox.AddHandler(InputElement.PointerPressedEvent, OnClipsListPointerPressed, RoutingStrategies.Tunnel);
        }

        TryConnectClipListScrollViewer();

        var searchTextBox = this.FindControl<TextBox>("SearchTextBox");
        if (searchTextBox is not null)
        {
            searchTextBox.GotFocus += OnSearchBoxGotFocus;
        }

        FocusSearchBox();
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
            m_subscribedViewModel = null;
        }

        if (m_settingsWindow is not null)
        {
            m_settingsWindow.Closing -= OnSettingsWindowClosing;
            try { m_settingsWindow.Close(); } catch { }
            m_settingsWindow = null;
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
            m_clipsListBox = null;
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

        if (TryHandleEditedClipShortcut(viewModel, e))
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

    private bool TryRedirectToSearchBox(KeyEventArgs e)
    {
        var searchTextBox = this.FindControl<TextBox>("SearchTextBox");
        if (searchTextBox is null || searchTextBox.IsFocused)
        {
            return false;
        }

        // Don't redirect if already in a text input
        if (e.Source is TextBox or AvaloniaEdit.TextEditor or Controls.SyntaxTextEditor)
        {
            return false;
        }

        // Only redirect unmodified or shift-modified printable keys
        var relevantModifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);
        if (relevantModifiers != KeyModifiers.None)
        {
            return false;
        }

        // Check for printable key
        if (e.Key is < Key.A or > Key.Z and < Key.D0 or > Key.D9
            and < Key.NumPad0 or > Key.NumPad9
            and not Key.Space and not Key.OemMinus and not Key.OemPlus
            and not Key.OemPeriod and not Key.OemComma)
        {
            return false;
        }

        searchTextBox.Focus();
        return false; // Let the key event propagate to the now-focused search box
    }

    private void OnClipsListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || m_clipsListBox is null
            || viewModel.SelectedClip is null)
        {
            return;
        }

        viewModel.CopySelectedCommand.Execute().Subscribe(_ => MinimizeWindow());
        e.Handled = true;
    }

    private void OnClipsListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || e.Source is not Avalonia.Visual sourceVisual)
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

        viewModel.CopySelectedCommand.Execute().Subscribe(_ => MinimizeWindow());
        return true;
    }

    private bool TryHandleEnterToCopyShortcut(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (viewModel.SelectedClip is null || viewModel.IsSettingsOpen || viewModel.IsWelcomeOpen || viewModel.SessionLogs.IsOpen)
        {
            return false;
        }

        if (e.Source is TextBox or AvaloniaEdit.TextEditor or Controls.SyntaxTextEditor)
        {
            return false;
        }

        var relevantModifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        if (e.Key != Key.Enter || relevantModifiers != KeyModifiers.None)
        {
            return false;
        }

        viewModel.CopySelectedCommand.Execute().Subscribe(_ => MinimizeWindow());
        return true;
    }

    private static bool TryHandleEditedClipShortcut(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (e.Source is not TextBox and not AvaloniaEdit.TextEditor and not Controls.SyntaxTextEditor)
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

    private bool TryHandleClipIndexShortcut(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (viewModel.IsSettingsOpen || viewModel.IsWelcomeOpen || viewModel.SessionLogs.IsOpen)
        {
            return false;
        }

        // Don't intercept when inside a text input
        if (e.Source is TextBox or AvaloniaEdit.TextEditor or Controls.SyntaxTextEditor)
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

        if (modifiers == KeyModifiers.Alt)
        {
            viewModel.SelectClipByIndex(index);
            return true;
        }

        return false;
    }

    private void MinimizeWindow()
    {
        Dispatcher.UIThread.Post(() => Hide());
    }

    private async void OnSearchBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.LoadRecentSearchesAsync();
        }
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
            m_subscribedViewModel = null;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            m_subscribedSessionLogs = viewModel.SessionLogs;
            m_subscribedSessionLogs.PropertyChanged += OnSessionLogsPropertyChanged;
            UpdateSessionLogsWindowVisibility(m_subscribedSessionLogs.IsOpen);

            m_subscribedViewModel = viewModel;
            m_subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateSettingsWindowVisibility(viewModel.IsSettingsOpen);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen) && sender is MainWindowViewModel vm)
        {
            Dispatcher.UIThread.Post(() => UpdateSettingsWindowVisibility(vm.IsSettingsOpen));
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
                m_settingsWindow.Activate();
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
                m_sessionLogsWindow.Activate();
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
        var searchTextBox = this.FindControl<TextBox>("SearchTextBox");
        if (searchTextBox is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => searchTextBox.Focus(), DispatcherPriority.Input);
    }
}
