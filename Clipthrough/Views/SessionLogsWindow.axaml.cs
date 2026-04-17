using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Clipthrough.ViewModels;

namespace Clipthrough.Views;

public partial class SessionLogsWindow : Window
{
    public SessionLogsWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnCopyAllLogsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SessionLogsViewModel viewModel)
            {
                return;
            }

            var text = viewModel.AllLogsAsText;
            var clipboard = Clipboard;
            if (clipboard is not null && !string.IsNullOrWhiteSpace(text))
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Copy logs failed: {ex}");
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
