using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Clipthrough.ViewModels;

namespace Clipthrough.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsPendingPlaintextEncryptionPasswordChange)
        {
            var confirmed = await ConfirmDialog.ShowAsync(
                this,
                Clipthrough.Localization.AppText.SettingsConfirmEncryptionPasswordTitle,
                Clipthrough.Localization.AppText.SettingsConfirmEncryptionPasswordBody,
                Clipthrough.Localization.AppText.SettingsConfirmEncryptionPasswordConfirm,
                Clipthrough.Localization.AppText.SettingsConfirmEncryptionPasswordCancel);
            if (!confirmed)
            {
                return;
            }
        }

        viewModel.SaveSettingsCommand.Execute().Subscribe();
    }

    private async void OnReencryptClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var storage = viewModel.GetStorageOptionsService();
        var ok = await Views.RekeyDialog.ShowAsync(this, storage);
        if (ok)
        {
            viewModel.NotifyStorageOptionsChanged();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.CloseSettingsCommand.Execute().Subscribe();
        }
    }
}
