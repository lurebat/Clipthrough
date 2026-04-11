using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.ViewModels;

namespace Clipthrough.Views;

public partial class MainWindow : Window
{
    private ScrollViewer? m_clipListScrollViewer;

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

        var clipsListBox = this.FindControl<ListBox>("ClipsListBox");
        m_clipListScrollViewer = clipsListBox?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
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
            return;
        }

        m_clipListScrollViewer.ScrollChanged -= OnClipListScrollChanged;
        m_clipListScrollViewer = null;
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

        if (!viewModel.TryHandleShortcut(e))
        {
            return;
        }

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