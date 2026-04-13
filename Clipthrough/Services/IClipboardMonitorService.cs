using System;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IClipboardMonitorService
{
    IObservable<ClipEntry> CapturedClips { get; }

    void Start();

    void Stop();

    /// <summary>
    /// Suppresses the next clipboard change capture. Call before app-initiated clipboard writes
    /// to prevent the monitor from re-capturing content the app itself placed on the clipboard.
    /// </summary>
    void SuppressNext();
}

