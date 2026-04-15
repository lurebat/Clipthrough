using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface ISystemInteractionService
{
    Task CopyTextAsync(string text);

    Task CopyRichContentAsync(string richContent, string plainText, ClipContentFormat contentFormat);

    Task CopyBitmapAsync(Bitmap bitmap);

    Task OpenPathAsync(string path);

    Task OpenContainingDirectoryAsync(string path);

    void ShowNotification(AppNotification notification);

    bool TryRegisterGlobalHotKey(Window window, HotkeyGesture hotkey, Action callback);

    bool TryRegisterGlobalHotKey(Window window, string name, HotkeyGesture hotkey, Action callback);

    void UnregisterGlobalHotKey();

    void UnregisterGlobalHotKey(string name);

    void UnregisterAllGlobalHotKeys();

    void SyncStartWithWindows(bool enabled);
}
