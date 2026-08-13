using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Clipthrough.Models;

namespace Clipthrough.Services;

/// <summary>
/// Disposable because the Windows implementation owns OS resources that outlive
/// the process if they are not released: a shell notification icon registered
/// with <c>Shell_NotifyIcon</c>, its hidden message window, and the global hotkey
/// registrations. Windows only reaps an orphaned notification icon when the user
/// next moves the mouse over the notification area, so skipping this leaves a
/// ghost icon behind after the app has exited.
/// </summary>
public interface ISystemInteractionService : IDisposable
{
    Task CopyTextAsync(string text);

    Task CopyRichContentAsync(string richContent, string plainText, ClipContentFormat contentFormat);

    Task CopyBitmapAsync(Bitmap bitmap);

    Task OpenPathAsync(string path);

    Task OpenUrlAsync(string url);

    Task OpenContainingDirectoryAsync(string path);

    Task OpenInEditorAsync(string filePath, string editorPath);

    Task OpenInDiffToolAsync(string leftPath, string rightPath, string diffToolPath);

    void CaptureTargetWindowForPaste();

    void ClearTargetWindowCapture();

    void RestoreCapturedForeground();

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
