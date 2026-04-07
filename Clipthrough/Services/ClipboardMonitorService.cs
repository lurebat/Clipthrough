using System;
using System.Reactive.Linq;
using AvaloniaApplication1.Models;

namespace AvaloniaApplication1.Services;

public sealed class ClipboardMonitorService : IClipboardMonitorService
{
    public IObservable<ClipEntry> CapturedClips { get; } = Observable.Never<ClipEntry>();

    public void Start()
    {
    }

    public void Stop()
    {
    }
}

