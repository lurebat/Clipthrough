using System;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IClipboardMonitorService
{
    IObservable<ClipEntry> CapturedClips { get; }

    IObservable<ClipEntry> UpdatedClips { get; }

    /// <summary>
    /// Emits true while a clipboard capture is being processed (clipboard
    /// COM read + database write + enrichment) and false when idle. UI can
    /// bind this to a busy indicator so the user sees feedback during the
    /// 1-2 second cases (e.g. large image copies from Photos / VS Code).
    /// </summary>
    IObservable<bool> CaptureBusy { get; }

    void Start();

    void Stop();

    /// <summary>
    /// Suppresses the next clipboard change capture. Call before app-initiated clipboard writes
    /// to prevent the monitor from re-capturing content the app itself placed on the clipboard.
    /// </summary>
    void SuppressNext();
}

