using System;
using System.Threading.Tasks;
using Avalonia;
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

    Task OpenInEditorAsync(string filePath, string editorPath);

    Task OpenInDiffToolAsync(string leftPath, string rightPath, string diffToolPath);

    void SimulatePasteKeystroke();

    void ShowNotification(AppNotification notification);

    bool TryRegisterGlobalHotKey(Window window, HotkeyGesture hotkey, Action callback);

    bool TryRegisterGlobalHotKey(Window window, string name, HotkeyGesture hotkey, Action callback);

    void UnregisterGlobalHotKey();

    void UnregisterGlobalHotKey(string name);

    void UnregisterAllGlobalHotKeys();

    PixelPoint? GetCaretScreenPosition();

    bool IsTargetWindowElevated();

    void SyncStartWithWindows(bool enabled);
}
