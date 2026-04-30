# Clipthrough Windows platform skill

Use this when touching anything that talks directly to Win32: clipboard formats, paste sequencing, global hotkeys, foreground-window handling, or input simulation. These patterns are easy to get subtly wrong; this file captures what currently works in the codebase.

## Files

- `Clipthrough/Services/Platform/SystemInteractionService.cs` — the primary `ISystemInteractionService` implementation. P/Invokes for clipboard, `SetForegroundWindow`, `AttachThreadInput`, `SendInput`, `RegisterHotKey`, `RegisterClipboardFormat`, etc.
- `Clipthrough/Services/Platform/WindowsSourceApplicationResolver.cs` — resolves the foreground-window owner to `(name, exe path, icon)`.
- `Clipthrough/Services/Platform/WindowsDataProtectionService.cs` — `ProtectedData` wrapper for password storage.

All Windows-only code is guarded with `[SupportedOSPlatform("windows")]` and falls back to `NullSourceApplicationResolver` / `NoOpDataProtectionService` on other OSes.

## Clipboard write contract

`ISystemInteractionService` exposes:

- `Task CopyTextAsync(string text)` — plain text only.
- `Task CopyRichContentAsync(string richContent, string plainTextFallback, ClipContentFormat format)` — rich payloads.

For `format == ClipContentFormat.Html`, `CopyRichContentAsync`:

1. Calls `RegisterClipboardFormat("HTML Format")` to get the CF_HTML format ID.
2. If the supplied string already looks like CF_HTML (`LooksLikeCfHtml(richContent)`), it's written as-is.
3. Otherwise it wraps the markup with `BuildCfHtml(html)` so the byte offsets in the CF_HTML header are correct. This is what makes Word/Outlook/Teams accept the paste as a real HTML document.
4. Always also writes the plain-text fallback so apps that don't understand HTML still get sensible text.

When you also want to insert the result into the active app, call `_clipboardMonitorService.SuppressNext()` **before** the write so the monitor doesn't immediately re-capture it as a new clip.

## Capturing the foreground app before showing the window

Showing the main window steals foreground focus, which would break Ctrl+V into "the app the user was just in". The pattern in `SystemInteractionService` is:

1. Tray-icon / global-hotkey path captures the current foreground HWND **before** showing the Clipthrough window. The HWND + thread id are stored on the service.
2. When the user picks a clip, the VM:
   - Writes the clip to the OS clipboard (`CopyTextAsync` / `CopyRichContentAsync`).
   - Calls `RestoreCapturedForeground()` to put focus back on the captured HWND.
   - Calls `SimulatePasteKeystroke()` to send Ctrl+V via `SendInput`.

`RestoreCapturedForeground` uses the AttachThreadInput trick — see below — because plain `SetForegroundWindow` will fail when the calling process doesn't own the foreground.

## AttachThreadInput + SetForegroundWindow

```csharp
var attached = currentThreadId != targetThreadId
    && AttachThreadInput(currentThreadId, targetThreadId, true);
try
{
    SetForegroundWindow(target);
}
finally
{
    if (attached)
    {
        AttachThreadInput(currentThreadId, targetThreadId, false);
    }
}
```

Always pair the `AttachThreadInput(true)` with a `false` in `finally`. Leaking attached input queues makes the user's keyboard feel "sticky" across windows.

Common ordering bugs (we hit each of these before — see commits `db1d332`, `2d68bce`, `09e4b80`):

- Calling `SetForegroundWindow` from a continuation after an `await` — by then the input handler has returned and Windows has revoked your right to set foreground. Set foreground while still synchronously inside the input event handler.
- Awaiting before `RestoreCapturedForeground` — same problem.
- Sending `SendInput` while still attached to the wrong thread — keystroke goes to the wrong window.

## SendInput struct layout

`INPUT` size differs between architectures and contains a `KEYBDINPUT` / `MOUSEINPUT` / `HARDWAREINPUT` union. We've been bitten by `Marshal.SizeOf<INPUT>()` returning the wrong number on x64 (32 instead of 24 in some configs). The fix that ships in this codebase:

- Use the explicit `cbSize` constant from the struct definition rather than `Marshal.SizeOf`.
- Include all three union members (`KEYBDINPUT`, `MOUSEINPUT`, `HARDWAREINPUT`) in `InputUnion`, even though we only ever populate `KEYBDINPUT`. This forces the correct overall size.

When `SendInput` returns fewer events than requested, log `Marshal.GetLastWin32Error()` — the most common causes are UIPI (the target window runs at a higher integrity level than us) or input being blocked by a `BlockInput` / secure-desktop transition.

## Global hotkeys

`TryRegisterGlobalHotKey(window, gesture, callback)`:

1. Resolves an HWND for the Avalonia `Window` via the platform handle.
2. Calls `Win32Properties.AddWndProcHookCallback(_window, _hookCallback)` to install a message hook. **Use the Avalonia helper** — never hold a managed `WndProc` delegate yourself, or it will be GC'd while Win32 still holds the function pointer and the process will crash on the next message.
3. Calls `RegisterHotKey(hWnd, id, modifiers, vk)` and dispatches `WM_HOTKEY` to the supplied callback.

Pair every successful `RegisterHotKey` with `UnregisterHotKey` on shutdown, and remove the hook callback. `Win32Properties.AddWndProcHookCallback` returns the registration; keep the reference alive as long as the hotkey is active.

## Source-app resolution

`WindowsSourceApplicationResolver` walks `GetForegroundWindow → GetWindowThreadProcessId → OpenProcess(QueryLimitedInformation) → QueryFullProcessImageName`, then loads the icon via `ExtractAssociatedIcon` / `Shell32`. It deliberately uses `PROCESS_QUERY_LIMITED_INFORMATION` (not `PROCESS_QUERY_INFORMATION`) so it works against elevated processes from a non-elevated Clipthrough.

When a process refuses to disclose its path (system processes, some store apps), fall back to the window class / `GetWindowText` instead of failing the capture.

## P/Invoke style

- Prefer `LibraryImport` (.NET 7+, supported on .NET 10) over `DllImport` for new declarations — it's source-generated and avoids marshalling reflection.
- Always specify `SetLastError = true` when you intend to log `Marshal.GetLastWin32Error()`.
- Match struct layouts exactly. `[StructLayout(LayoutKind.Sequential)]` for plain structs, `[StructLayout(LayoutKind.Explicit)]` with `[FieldOffset(0)]` for unions.
- Pin or copy buffers across `await`s — never pass a Span/ref directly across an awaiter.

## Quick reference: what to call when

| Goal                                                       | API                                                                  |
| ---------------------------------------------------------- | -------------------------------------------------------------------- |
| Plain text → clipboard                                     | `ISystemInteractionService.CopyTextAsync(text)`                      |
| HTML → clipboard (Teams/Outlook paste-as-table works)      | `CopyRichContentAsync(html, plainFallback, ClipContentFormat.Html)`  |
| RTF → clipboard                                            | `CopyRichContentAsync(rtf, plainFallback, ClipContentFormat.Rtf)`    |
| Restore the user's previous app and paste                  | `RestoreCapturedForeground()` then `SimulatePasteKeystroke()`        |
| Bind a global hotkey                                       | `TryRegisterGlobalHotKey(window, gesture, callback)`                 |
| Capture which app was foregrounded                         | `WindowsSourceApplicationResolver.Resolve()`                         |
| Encrypt secrets at rest (per-user)                         | `IDataProtectionService.Protect(bytes)` / `.Unprotect(bytes)`        |

## Don't

- Don't call `SetClipboardData` directly from arbitrary threads. The clipboard is a single-threaded resource — go through Avalonia's clipboard or `SystemInteractionService` so we centralise retries on `OpenClipboard` failures.
- Don't `Thread.Sleep` between `RestoreCapturedForeground` and `SimulatePasteKeystroke` for "more than ~50 ms" — long delays let the user move focus and the paste lands in the wrong window.
- Don't catch and swallow `SEHException` / `AccessViolationException` from interop. Let them surface — they always indicate a marshalling bug worth fixing.
