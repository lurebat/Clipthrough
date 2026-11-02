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
        WindowFitter.Attach(this);
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

        // A dialog follows its owner's motion policy (MainWindow.Motion.cs).
        dialog.Classes.Set(MainWindow.MotionClass, owner.Classes.Contains(MainWindow.MotionClass));
        return dialog.ShowDialog<bool>(owner);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
