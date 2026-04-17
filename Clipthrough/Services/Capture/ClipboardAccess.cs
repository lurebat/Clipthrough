using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
namespace Clipthrough.Services;
internal static class ClipboardAccess
{
    public static Avalonia.Input.Platform.IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }
        var window = desktop.MainWindow ?? desktop.Windows.FirstOrDefault(static candidate => candidate.IsVisible);
        return window?.Clipboard;
    }
}
