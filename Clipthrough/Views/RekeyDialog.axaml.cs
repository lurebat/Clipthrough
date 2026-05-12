using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Clipthrough.Services;

namespace Clipthrough.Views;

public partial class RekeyDialog : Window
{
    private IStorageOptionsService? _storageOptionsService;
    private bool _busy;

    public RekeyDialog()
    {
        InitializeComponent();

        var rememberBox = this.FindControl<CheckBox>("RememberPasswordBox")!;
        var warning = this.FindControl<TextBlock>("WarningText")!;
        warning.IsVisible = rememberBox.IsChecked == true;
        rememberBox.IsCheckedChanged += (_, _) =>
            warning.IsVisible = rememberBox.IsChecked == true;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public static Task<bool> ShowAsync(Window owner, IStorageOptionsService storageOptionsService)
    {
        var dialog = new RekeyDialog
        {
            _storageOptionsService = storageOptionsService,
        };
        var rememberBox = dialog.FindControl<CheckBox>("RememberPasswordBox")!;
        rememberBox.IsChecked = storageOptionsService.Current.RememberPassword;
        var currentBox = dialog.FindControl<TextBox>("CurrentPasswordBox")!;
        currentBox.Text = storageOptionsService.Current.DatabasePassword ?? string.Empty;
        return dialog.ShowDialog<bool>(owner);
    }

    private async void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _storageOptionsService is null)
        {
            return;
        }

        var current = this.FindControl<TextBox>("CurrentPasswordBox")!.Text ?? string.Empty;
        var newPwd = this.FindControl<TextBox>("NewPasswordBox")!.Text ?? string.Empty;
        var confirm = this.FindControl<TextBox>("ConfirmPasswordBox")!.Text ?? string.Empty;
        var remember = this.FindControl<CheckBox>("RememberPasswordBox")!.IsChecked == true;
        var error = this.FindControl<TextBlock>("ErrorText")!;
        var apply = this.FindControl<Button>("ApplyButton")!;
        var cancel = this.FindControl<Button>("CancelButton")!;

        if (!string.Equals(newPwd, confirm, StringComparison.Ordinal))
        {
            error.Text = "New password and confirmation do not match.";
            error.IsVisible = true;
            return;
        }

        _busy = true;
        apply.IsEnabled = false;
        cancel.IsEnabled = false;
        error.IsVisible = false;

        try
        {
            await _storageOptionsService.RekeyAsync(current, newPwd, remember);
            Close(true);
        }
        catch (Exception ex)
        {
            error.Text = ex.Message;
            error.IsVisible = true;
        }
        finally
        {
            _busy = false;
            apply.IsEnabled = true;
            cancel.IsEnabled = true;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
