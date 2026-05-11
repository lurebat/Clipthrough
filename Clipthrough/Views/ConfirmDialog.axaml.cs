using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Clipthrough.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public static Task<bool> ShowAsync(
        Window owner,
        string title,
        string body,
        string confirmLabel,
        string cancelLabel)
    {
        var dialog = new ConfirmDialog
        {
            Title = title,
        };

        dialog.FindControl<TextBlock>("TitleText")!.Text = title;
        dialog.FindControl<TextBlock>("BodyText")!.Text = body;
        dialog.FindControl<Button>("ConfirmButton")!.Content = confirmLabel;
        dialog.FindControl<Button>("CancelButton")!.Content = cancelLabel;

        return dialog.ShowDialog<bool>(owner);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
