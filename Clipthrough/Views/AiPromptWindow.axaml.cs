using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Clipthrough.ViewModels;

namespace Clipthrough.Views;

public partial class AiPromptWindow : Window
{
    public AiPromptWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Opened += OnOpened;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        var promptBox = this.FindControl<TextBox>("AiPromptInputTextBox");
        Dispatcher.UIThread.Post(() =>
        {
            promptBox?.Focus();
            if (promptBox is not null)
            {
                promptBox.CaretIndex = promptBox.Text?.Length ?? 0;
            }
        }, DispatcherPriority.Input);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.CancelAiPromptCommand.Execute().Subscribe();
            }
            else
            {
                Close();
            }
            return;
        }

        if (e.Key != Key.Enter && e.Key != Key.Return)
        {
            return;
        }

        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        if (modifiers == KeyModifiers.Shift)
        {
            return;
        }

        if (modifiers == KeyModifiers.Control && e.Source is TextBox textBox)
        {
            InsertNewLine(textBox);
            e.Handled = true;
            return;
        }

        if (modifiers != KeyModifiers.None)
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            e.Handled = true;
            vm.SubmitAiPromptCommand.Execute().Subscribe();
        }
    }

    private static void InsertNewLine(TextBox textBox)
    {
        var text = textBox.Text ?? string.Empty;
        var selectionStart = Math.Clamp(textBox.SelectionStart, 0, text.Length);
        var selectionEnd = Math.Clamp(textBox.SelectionEnd, 0, text.Length);
        var start = Math.Min(selectionStart, selectionEnd);
        var end = Math.Max(selectionStart, selectionEnd);
        textBox.Text = text[..start] + Environment.NewLine + text[end..];
        textBox.CaretIndex = start + Environment.NewLine.Length;
    }
}
