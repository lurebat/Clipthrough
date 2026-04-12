using System;
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
    private ListBox? m_sessionLogsListBox;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
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
        m_sessionLogsListBox = this.FindControl<ListBox>("SessionLogsListBox");
        if (m_clipsListBox is not null)
        {
            m_clipsListBox.AddHandler(InputElement.DoubleTappedEvent, OnClipsListDoubleTapped, RoutingStrategies.Bubble);
            m_clipsListBox.AddHandler(InputElement.PointerPressedEvent, OnClipsListPointerPressed, RoutingStrategies.Tunnel);
        }

        if (m_sessionLogsListBox is not null)
        {
            m_sessionLogsListBox.AddHandler(InputElement.DoubleTappedEvent, OnSessionLogsDoubleTapped, RoutingStrategies.Bubble);
        }

        m_clipListScrollViewer = m_clipsListBox?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (m_clipListScrollViewer is not null)
        {
            m_clipListScrollViewer.ScrollChanged += OnClipListScrollChanged;
        }

        FocusSearchBox();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (m_clipListScrollViewer is null)
        {
            if (m_clipsListBox is not null)
            {
                m_clipsListBox.RemoveHandler(InputElement.DoubleTappedEvent, OnClipsListDoubleTapped);
                m_clipsListBox = null;
            }

            if (m_sessionLogsListBox is not null)
            {
                m_sessionLogsListBox.RemoveHandler(InputElement.DoubleTappedEvent, OnSessionLogsDoubleTapped);
                m_sessionLogsListBox = null;
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

        if (m_sessionLogsListBox is not null)
        {
            m_sessionLogsListBox.RemoveHandler(InputElement.DoubleTappedEvent, OnSessionLogsDoubleTapped);
            m_sessionLogsListBox = null;
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

        if (TryHandleClipRecopyShortcut(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        if (!viewModel.TryHandleShortcut(e))
        {
            return;
        }

        e.Handled = true;
    }

    private void OnClipsListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || m_clipsListBox is null
            || viewModel.SelectedClip is null)
        {
            return;
        }

        viewModel.CopySelectedCommand.Execute().Subscribe();
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

    private void OnSessionLogsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual sourceVisual)
        {
            return;
        }

        var dataContextElement = sourceVisual.GetSelfAndVisualAncestors()
            .OfType<StyledElement>()
            .FirstOrDefault(current => current.DataContext is SessionLogEntryViewModel);
        if (dataContextElement?.DataContext is not SessionLogEntryViewModel logEntry)
        {
            return;
        }

        logEntry.ToggleExpanded();
        e.Handled = true;
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

        viewModel.CopySelectedCommand.Execute().Subscribe();
        return true;
    }

    private static bool TryHandleEditedClipShortcut(MainWindowViewModel viewModel, KeyEventArgs e)
    {
        if (e.Source is not TextBox)
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

    private void FocusSearchBox()
    {
        var searchTextBox = this.FindControl<TextBox>("SearchTextBox");
        if (searchTextBox is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => searchTextBox.Focus(), DispatcherPriority.Input);
    }
}
