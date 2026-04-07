using System;
using System.Reactive.Linq;
using Clipthrough.Models;

namespace Clipthrough.Services;

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

