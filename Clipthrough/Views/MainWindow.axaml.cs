using System;
using System.Linq;
using Avalonia.Controls;
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
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (m_clipListScrollViewer is not null)
        {
            return;
        }

        var clipsListBox = this.FindControl<ListBox>("ClipsListBox");
        m_clipListScrollViewer = clipsListBox?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (m_clipListScrollViewer is not null)
        {
            m_clipListScrollViewer.ScrollChanged += OnClipListScrollChanged;
        }
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
}