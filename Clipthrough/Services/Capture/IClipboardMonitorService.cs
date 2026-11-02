using System;
using System.Threading.Tasks;
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
    /// Stops capturing and does not return until nothing clipboard-originated is
    /// still writing to the database.
    /// </summary>
    /// <remarks>
    /// <see cref="Stop"/> is not enough for that. Off the UI thread it only posts
    /// the stop and returns, so a caller that is about to move, rekey or replace
    /// the database file can clear the connection pools while a capture is still
    /// mid-write. Background enrichment - the deferred content update, the
    /// sensitivity scan, the icon write, the retention pass - is started
    /// fire-and-forget and outlives the capture that spawned it, so even a
    /// synchronous stop leaves writes in flight.
    ///
    /// Await this before touching database files. <see cref="Stop"/> remains the
    /// right call for an ordinary pause, where waiting would only add latency.
    /// </remarks>
    Task StopAsync();

    /// <summary>
    /// Suppresses the next clipboard change capture. Call before app-initiated clipboard writes
    /// to prevent the monitor from re-capturing content the app itself placed on the clipboard.
    /// </summary>
    /// <param name="content">
    /// The text about to be written, when there is any. A later notification
    /// carrying the same text within the suppression window is treated as a
    /// re-publish of this write (Remote Desktop does that) and not captured.
    /// </param>
    void SuppressNext(string? content = null);

    /// <summary>
    /// Skips the next clipboard change whichever application makes it. For the
    /// copy-without-saving hotkey, where the copy that must not be kept is the
    /// one the user is about to make in another application.
    /// </summary>
    /// <remarks>
    /// <see cref="SuppressNext"/> is for Clipthrough's own writes, and only
    /// suppresses while Clipthrough owns the clipboard, so another application's
    /// copy landing in the same moment is still captured. This one is not
    /// limited that way, because swallowing another application's copy is its
    /// purpose.
    /// </remarks>
    void SuppressNextForeignCopy();

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

