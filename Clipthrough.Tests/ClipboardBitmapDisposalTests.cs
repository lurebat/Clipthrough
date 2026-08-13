using System;
using System.Linq;
using System.Reflection;
using Avalonia.Media.Imaging;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests;

/// <summary>
/// An Avalonia Bitmap owns a native surface. The capture path pulls one out of the
/// clipboard for every image copy, and used to drop it on the floor - the vendored
/// ShareX clipboard handler in this same repository disposes its own for exactly
/// this reason.
/// </summary>
public class ClipboardBitmapDisposalTests
{
    [Fact]
    public void BuildCaptureRequest_DisposesTheBitmapItTakesFromTheClipboard()
    {
        var build = typeof(ClipboardMonitorService)
            .GetMethod("BuildCaptureRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static)!;

        var disposeMethods = new[]
        {
            typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!,
            typeof(Bitmap).GetMethod(nameof(Bitmap.Dispose), Type.EmptyTypes),
        }.Where(m => m is not null).Select(m => m!).Distinct().ToArray();

        var disposals = disposeMethods.Sum(m => IlCallScanner.CountCallsIn(build, m));

        // The bitmap is the only thing this method owns - the data transfer object
        // belongs to the caller - so zero disposals means the bitmap is leaking again.
        Assert.True(disposals > 0, "the bitmap taken from the clipboard is never disposed");
    }
}
