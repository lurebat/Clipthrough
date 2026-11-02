using System;
using System.Linq;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Skia;
using Clipthrough.Presentation;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Live check G1: undecodable image bytes made ClipBitmapFactory.TryLoad throw a
/// NullReferenceException from inside Avalonia.Skia on the scaled-decode path
/// (DecodeToWidth), so the row thumbnail task faulted unobserved instead of
/// falling back. Skia's SKCodec.Create returns null for data it does not
/// recognise, and ImmutableBitmap(Stream, decodeSize, ...) reads codec.Info
/// without checking; the full-size path checks and throws ArgumentException.
/// </summary>
/// <remarks>
/// The test application draws with the headless stub, which never decodes - it
/// accepts any bytes. So this test swaps in the real Skia renderer for its own
/// duration and restores both services SkiaPlatform.Initialize binds. The PNG
/// control proves the swap took effect: without it the undecodable case would
/// pass on the stub no matter what TryLoad does.
/// </remarks>
public sealed class ClipBitmapFactoryHeadlessTests
{
    // A 1x1 transparent PNG.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static readonly byte[] Undecodable = Enumerable.Range(0, 64).Select(static i => (byte)((i * 7) + 3)).ToArray();

    [AvaloniaFact]
    public void TryLoad_ReturnsNull_ForBytesSkiaCannotDecode_OnBothDecodePaths()
    {
        // AvaloniaLocator's members are public at runtime but absent from the
        // reference assemblies (private API), so the scope is entered by reflection.
        // A scope holds the bindings SkiaPlatform.Initialize makes, and disposing it
        // restores the stub renderer for every later test.
        var enterScope = typeof(AvaloniaLocator).GetMethod("EnterScope", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("AvaloniaLocator.EnterScope is gone; this test can no longer swap in the Skia renderer.");
        using var scope = (IDisposable)enterScope.Invoke(null, null)!;
        SkiaPlatform.Initialize();

        using (var control = ClipBitmapFactory.TryLoad(TinyPng, decodeWidth: 4))
        {
            Assert.NotNull(control);
        }

        Assert.Null(ClipBitmapFactory.TryLoad(Undecodable, decodeWidth: 48));
        Assert.Null(ClipBitmapFactory.TryLoad(Undecodable));
    }
}
