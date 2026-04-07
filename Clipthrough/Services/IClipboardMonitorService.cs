using System;
using AvaloniaApplication1.Models;

namespace AvaloniaApplication1.Services;

public interface IClipboardMonitorService
{
    IObservable<ClipEntry> CapturedClips { get; }

    void Start();

    void Stop();
}

