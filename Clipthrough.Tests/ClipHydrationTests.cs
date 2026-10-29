using System;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Clipthrough.Tests;

/// <summary>
/// Hydration reports whether it loaded anything, and the preview rebuild depends
/// on that answer.
/// </summary>
/// <remarks>
/// The preview is built once when the selection changes and again when hydration
/// finishes. The second rebuild used to run unconditionally, so every text clip
/// rendered twice with identical inputs - BuildRenderedText over the whole
/// content, on every arrow key. It now runs only when hydration actually loaded
/// something, which makes this return value load-bearing: report false after a
/// real load and a selected image would stay blank until an unrelated refresh
/// repainted it.
///
/// Tested here rather than through the window because the list read in the test
/// harness returns image bytes inline, so the state hydration exists to repair
/// never arises there - an earlier attempt asserted nothing for that reason and
/// said so out loud rather than passing quietly.
///
/// AvaloniaFact, not Fact: hydration marshals its result with
/// Dispatcher.UIThread.InvokeAsync, which never completes without a dispatcher
/// loop. As a plain Fact these tests hang rather than fail, which reads as a
/// stuck build rather than a broken test.
/// </remarks>
public sealed class ClipHydrationTests
{
    private static ClipEntry ImageEntry(byte[]? bytes) => new()
    {
        Id = 77,
        Content = "an image",
        ContentBytes = bytes,
        ContentType = ContentType.Image,
        ContentFormat = ClipContentFormat.Bitmap,
        SourceApp = "Tests",
        Hash = "hash-image",
        LastCopiedAt = DateTimeOffset.UtcNow,
        FirstCopiedAt = DateTimeOffset.UtcNow,
    };

    [AvaloniaFact]
    public async Task AnImageMissingItsBytes_HydratesAndSaysSo()
    {
        var full = ImageEntry(Encoding.UTF8.GetBytes("the real bytes"));
        var calls = 0;
        var vm = new ClipItemViewModel(ImageEntry(null), contentHydrator: id =>
        {
            calls++;
            return Task.FromResult<ClipEntry?>(full);
        });

        Assert.True(await vm.EnsureContentHydratedAsync(), "hydration loaded the row but reported that it had not");
        Assert.Equal(1, calls);
        Assert.Equal(full.ContentBytes, vm.Clip.ContentBytes);
    }

    /// <summary>
    /// The case the optimisation relies on: a clip that needs nothing must say
    /// so, or the preview rebuild it triggers is pure waste.
    /// </summary>
    [AvaloniaFact]
    public async Task AClipThatNeedsNothing_ReportsNoHydration()
    {
        var calls = 0;
        var text = new ClipEntry
        {
            Id = 78,
            Content = "just text",
            ContentBytes = Encoding.UTF8.GetBytes("just text"),
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = "Tests",
            Hash = "hash-text",
            LastCopiedAt = DateTimeOffset.UtcNow,
            FirstCopiedAt = DateTimeOffset.UtcNow,
        };
        var vm = new ClipItemViewModel(text, contentHydrator: id => { calls++; return Task.FromResult<ClipEntry?>(null); });

        Assert.False(await vm.EnsureContentHydratedAsync());
        Assert.Equal(0, calls);
    }

    /// <summary>
    /// A hydrator that comes back empty must not claim it loaded something.
    /// </summary>
    [AvaloniaFact]
    public async Task AHydratorThatFindsNothing_ReportsNoHydration()
    {
        var vm = new ClipItemViewModel(ImageEntry(null), contentHydrator: id => Task.FromResult<ClipEntry?>(null));

        Assert.False(await vm.EnsureContentHydratedAsync());
    }

    private static ClipEntry LongTextRow(bool truncated) => new()
    {
        Id = 78,
        Content = truncated ? new string('a', ClipEntry.ListContentLength) : new string('a', ClipEntry.ListContentLength) + "the rest",
        IsContentTruncated = truncated,
        ContentType = ContentType.Text,
        ContentFormat = ClipContentFormat.PlainText,
        SourceApp = "Tests",
        Hash = "hash-long",
        LastCopiedAt = DateTimeOffset.UtcNow,
        FirstCopiedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>F-10: a row whose text the list read bounded is hydrated like a missing blob.</summary>
    [AvaloniaFact]
    public async Task ATruncatedTextRow_Hydrates()
    {
        var vm = new ClipItemViewModel(LongTextRow(truncated: true), contentHydrator: _ => Task.FromResult<ClipEntry?>(LongTextRow(truncated: false)));

        Assert.True(await vm.EnsureContentHydratedAsync());
        Assert.EndsWith("the rest", vm.Clip.Content, StringComparison.Ordinal);
        Assert.EndsWith("the rest", vm.FullContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Copy runs right after selection, while selection's hydration is still in
    /// flight. It has to wait for that hydration, not be told "nothing to do" and
    /// read the row as it stands.
    /// </summary>
    [AvaloniaFact]
    public async Task ASecondCaller_WaitsForTheHydrationAlreadyRunning()
    {
        var gate = new TaskCompletionSource<ClipEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var vm = new ClipItemViewModel(LongTextRow(truncated: true), contentHydrator: _ =>
        {
            calls++;
            return gate.Task;
        });

        var first = vm.EnsureContentHydratedAsync();
        var second = vm.EnsureContentHydratedAsync();
        Assert.False(second.IsCompleted, "the second caller was answered before the running hydration finished");

        gate.SetResult(LongTextRow(truncated: false));
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, calls);
        Assert.False(vm.Clip.IsContentTruncated);
    }
}