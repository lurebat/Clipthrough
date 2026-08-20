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

    /// <summary>
    /// True between <see cref="Start"/> and <see cref="Stop"/>. Lets callers that
    /// quiesce the monitor for a whole-database operation restore the state they
    /// found rather than unconditionally starting it.
    /// </summary>
    bool IsRunning { get; }

    void Start();

    void Stop();

    /// <summary>
    /// Suppresses the next clipboard change capture. Call before app-initiated clipboard writes
    /// to prevent the monitor from re-capturing content the app itself placed on the clipboard.
    /// </summary>
    void SuppressNext();

    /// <summary>
    /// Withdraws a suppression armed by <see cref="SuppressNext"/> when the write it was
    /// armed for threw before reaching the clipboard.
    /// </summary>
    /// <remarks>
    /// Only call this when no write landed. A suppression that is armed and never consumed
    /// is spent on whatever the user copies next, which is then missing from their history
    /// with nothing to explain it. The suppression window bounds that to a couple of
    /// seconds on its own; this closes it outright for the case the caller can be sure of.
    /// </remarks>
    void CancelSuppressNext();
}

