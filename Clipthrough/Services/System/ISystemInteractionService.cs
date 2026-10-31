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

    /// <summary>
    /// Waits until the window a paste is aimed at holds the foreground - the one
    /// <see cref="RestoreCapturedForeground"/> last handed it to, or, when there
    /// was none, any window that is not Clipthrough's - or until
    /// <paramref name="timeout"/>. Returns whether it got there.
    /// </summary>
    Task<bool> WaitForPasteTargetAsync(TimeSpan timeout, System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes <paramref name="window"/> out of sight without hiding it: DWM
    /// cloaks it, so it keeps rendering and the next uncloak shows a frame that
    /// is already current. With <paramref name="releaseForeground"/> the window
    /// also gives up the foreground, which the OS hands to whatever it would
    /// have activated had the window been hidden.
    /// </summary>
    /// <returns>False when cloaking is unavailable (not Windows, or DWM refused);
    /// the caller must hide the window instead.</returns>
    bool TryCloakWindow(Window window, bool releaseForeground);

    /// <summary>Reverses <see cref="TryCloakWindow"/>.</summary>
    void UncloakWindow(Window window);

    /// <summary>
    /// Whether the OS wants UI animations: Windows' Animation effects switch
    /// (SPI_GETCLIENTAREAANIMATION). True where there is no such signal.
    /// </summary>
    bool AreOsAnimationsEnabled();

    /// <summary>
    /// Calls <paramref name="onChanged"/> whenever the OS reports a settings
    /// change on <paramref name="window"/>'s message loop, so the answer above
    /// can be re-read. Dispose to stop.
    /// </summary>
    IDisposable WatchOsAnimationSetting(Window window, Action onChanged);

    void SimulatePasteKeystroke();

    void ShowNotification(AppNotification notification);

    bool TryRegisterGlobalHotKey(Window window, HotkeyGesture hotkey, Action callback);

    bool TryRegisterGlobalHotKey(Window window, string name, HotkeyGesture hotkey, Action callback);

    void UnregisterGlobalHotKey();

    void UnregisterGlobalHotKey(string name);

    void UnregisterAllGlobalHotKeys();

    PixelPoint? GetCaretScreenPosition();

    bool IsTargetWindowElevated();

    /// <summary>
    /// The user turned Start with Windows on or off: write this configuration's
    /// entry, or remove the entry.
    /// </summary>
    void SyncStartWithWindows(bool enabled);

    /// <summary>
    /// Applies the saved setting at startup, touching the entry only when it is
    /// missing or belongs to this configuration (same executable, same
    /// --data-dir). Another configuration's entry is left alone either way.
    /// </summary>
    void ReconcileStartWithWindows(bool enabled);
}
