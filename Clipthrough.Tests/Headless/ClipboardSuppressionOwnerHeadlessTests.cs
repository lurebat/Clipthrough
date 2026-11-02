using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Which clipboard changes a pending suppression may swallow (F-05-4).
/// </summary>
/// <remarks>
/// Clipthrough arms a suppression before each of its own clipboard writes, and
/// the monitor coalesces change notifications for 60 ms before reading. Another
/// application's copy inside that window used to fall into the same batch and
/// be dropped unread, so it never reached the history. A suppression for our
/// own write now holds only while Clipthrough owns the clipboard; the
/// copy-without-saving arm still swallows whoever copies, because that is what
/// it is for.
///
/// Each test drives one coalesced capture against a headless window's real
/// clipboard, so the decision under test is the monitor's, not a stub's.
/// </remarks>
public sealed class ClipboardSuppressionOwnerHeadlessTests
{
    private const string OurWrite = "pasted by Clipthrough";
    private const string TheirCopy = "copied in another app";

    private sealed class OwnerResolver(int? ownerProcessId) : ISourceApplicationResolver
    {
        public ClipboardSourceApplicationInfo? TryResolve(bool includeIcon = true) => null;

        public byte[]? TryResolveIcon(string? processPath) => null;

        public int? TryGetClipboardOwnerProcessId() => ownerProcessId;
    }

    private static readonly int AnotherProcess = Environment.ProcessId + 1;

    private static async Task<List<string>> CaptureOnceAsync(int? clipboardOwner, Func<ClipboardMonitorService, IClipboard, Task> act)
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var captured = new List<string>();
        var window = new Window();
        window.Show();
        try
        {
            using var monitor = new ClipboardMonitorService(
                scope.ClipStoreService, new OwnerResolver(clipboardOwner), new TestNotificationService(), new TestSettingsService());
            using var subscription = monitor.CapturedClips.Subscribe(clip => captured.Add(clip.Content));
            monitor.AttachForTests(window);

            await act(monitor, window.Clipboard!);
            await monitor.HandleClipboardChangedAsync();
            Dispatcher.UIThread.RunJobs();

            // Drains the enrichment a capture starts, before the database goes.
            await monitor.StopAsync();
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        return captured;
    }

    /// <summary>
    /// The lost copy: Clipthrough writes, another application copies before the
    /// coalesced read, and the one capture that follows is the other
    /// application's. It used to be swallowed by our own write's suppression.
    /// </summary>
    [AvaloniaFact]
    public async Task AnotherAppsCopyInsideTheCoalesceWindow_AfterOurOwnWrite_IsCaptured()
    {
        var captured = await CaptureOnceAsync(AnotherProcess, async (monitor, clipboard) =>
        {
            monitor.SuppressNext(OurWrite);
            await clipboard.SetTextAsync(OurWrite);
            await clipboard.SetTextAsync(TheirCopy);
        });

        Assert.Equal(new[] { TheirCopy }, captured);
    }

    /// <summary>The control: our own write, still ours, is not captured.</summary>
    [AvaloniaFact]
    public async Task OurOwnWrite_WhileWeOwnTheClipboard_IsStillSuppressed()
    {
        var captured = await CaptureOnceAsync(Environment.ProcessId, async (monitor, clipboard) =>
        {
            monitor.SuppressNext(OurWrite);
            await clipboard.SetTextAsync(OurWrite);
        });

        Assert.Empty(captured);
    }

    /// <summary>
    /// An owner that cannot be determined keeps the old behaviour: the
    /// suppression holds, rather than risk capturing our own write.
    /// </summary>
    [AvaloniaFact]
    public async Task AnUnknownClipboardOwner_KeepsTheSuppression()
    {
        var captured = await CaptureOnceAsync(clipboardOwner: null, async (monitor, clipboard) =>
        {
            monitor.SuppressNext(OurWrite);
            await clipboard.SetTextAsync(TheirCopy);
        });

        Assert.Empty(captured);
    }

    /// <summary>
    /// Copy-without-saving arms the gate so the user's next copy - made in
    /// another application - is not kept. The owner check must not undo that.
    /// </summary>
    [AvaloniaFact]
    public async Task CopyWithoutSaving_StillSwallowsAnotherAppsCopy()
    {
        var captured = await CaptureOnceAsync(AnotherProcess, async (monitor, clipboard) =>
        {
            monitor.SuppressNextForeignCopy();
            await clipboard.SetTextAsync(TheirCopy);
        });

        Assert.Empty(captured);
    }
}
