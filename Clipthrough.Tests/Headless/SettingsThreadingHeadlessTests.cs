using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The settings dialog saves inside <c>Task.Run</c>, so <c>SaveAsync</c> — and
/// therefore the <c>SettingsChanged</c> event it raises — ran entirely on a
/// thread-pool thread. <c>App.OnSettingsChanged</c> reacts by mutating
/// <c>Window.KeyBindings</c> and calling Win32 <c>RegisterHotKey</c>.
///
/// <c>RegisterHotKey</c> binds the hotkey to the <em>calling thread's</em>
/// message queue, and a pool thread has no message pump, so <c>WM_HOTKEY</c>
/// was delivered to a queue nobody reads: every global hotkey silently stopped
/// working the moment the user saved settings, and stayed dead until restart.
/// Nothing failed, nothing was logged.
/// </summary>
public sealed class SettingsThreadingHeadlessTests
{
    private static SettingsService NewService(TemporaryDatabaseScope scope)
        => new(
            scope.ConnectionFactory,
            new FakeDataProtectionService(),
            Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "settings.json"));

    [AvaloniaFact]
    public async Task SaveAsync_FromABackgroundThread_RaisesSettingsChangedOnTheUIThread()
    {
        using var scope = new TemporaryDatabaseScope();
        var service = NewService(scope);

        bool? raisedOnUiThread = null;
        var raiseCount = 0;
        service.SettingsChanged += (_, _) =>
        {
            raiseCount++;
            raisedOnUiThread = Dispatcher.UIThread.CheckAccess();
        };

        // Task.Run mirrors how the settings dialog saves.
        await Task.Run(() => service.SaveAsync(AppSettings.Default with { ThemeMode = ThemeMode.Dark }));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, raiseCount);
        Assert.True(
            raisedOnUiThread,
            "SettingsChanged was raised off the UI thread; hotkey registration there binds WM_HOTKEY to a queue with no pump.");
    }

    /// <summary>
    /// The settings a subscriber receives must be the ones that raised it. A
    /// deferred raise that reads the live field instead of a snapshot delivers
    /// whatever the newest save installed to every queued subscriber, so an
    /// earlier save is reported as the later one.
    ///
    /// Both saves have to be queued before anything drains them, so this blocks
    /// the dispatcher thread rather than awaiting — an <c>await</c> here lets
    /// the dispatcher pump between the two saves and the test stops being able
    /// to tell a snapshot from a live read. Blocking is only safe because the
    /// raise uses <c>Post</c>; an <c>InvokeAsync</c> would deadlock right here.
    /// </summary>
    [AvaloniaFact]
    public void TwoBackgroundSaves_EachDeliverTheirOwnSettings()
    {
        using var scope = new TemporaryDatabaseScope();
        var service = NewService(scope);

        var seen = new List<ThemeMode>();
        service.SettingsChanged += (_, settings) => seen.Add(settings.ThemeMode);

        using var saved = new ManualResetEventSlim();
        _ = Task.Run(async () =>
        {
            await service.SaveAsync(AppSettings.Default with { ThemeMode = ThemeMode.Light });
            await service.SaveAsync(AppSettings.Default with { ThemeMode = ThemeMode.Dark });
            saved.Set();
        });

        Assert.True(saved.Wait(TimeSpan.FromSeconds(30)), "The background saves never completed.");
        Assert.Empty(seen);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { ThemeMode.Light, ThemeMode.Dark }, seen);
    }

    /// <summary>
    /// Saving from the UI thread must still notify synchronously; deferring it
    /// unconditionally would let a caller observe stale state right after an
    /// awaited save.
    /// </summary>
    [AvaloniaFact]
    public async Task SaveAsync_OnTheUIThread_RaisesSettingsChangedBeforeItReturns()
    {
        using var scope = new TemporaryDatabaseScope();
        var service = NewService(scope);

        var raised = false;
        service.SettingsChanged += (_, _) => raised = true;

        await service.SaveAsync(AppSettings.Default with { ThemeMode = ThemeMode.Dark });

        Assert.True(raised, "A save on the UI thread should not need a dispatcher turn to notify.");
    }

    /// <summary>
    /// The argument a subscriber receives is a historical snapshot, not the
    /// current state, and this pins the gap so nobody assumes otherwise.
    ///
    /// Delivery is also not order-preserving: a background save posts its
    /// snapshot to the dispatcher queue while a UI-thread save skips the queue
    /// and notifies inline, so a save that happened first can be delivered
    /// second. Both paths are live in this app - filter state is persisted from
    /// a pool thread, while a command-driven save resumes on the UI thread
    /// because SettingsService does not use ConfigureAwait(false). That
    /// reordering is real but depends on whether the UI-thread save's internal
    /// awaits happen to complete synchronously, so it is not asserted here;
    /// what is asserted below holds every time.
    ///
    /// App.OnSettingsChanged therefore ignores the argument and reads Current:
    /// it configures process-wide state - the theme, the startup registration,
    /// the global hotkeys - where newest always wins.
    /// </summary>
    [AvaloniaFact]
    public void ADeliveredSettingsSnapshot_CanAlreadyBeStaleWhenTheHandlerRuns()
    {
        using var scope = new TemporaryDatabaseScope();
        var service = NewService(scope);

        var seen = new List<(ThemeMode Delivered, ThemeMode Current)>();
        service.SettingsChanged += (_, settings) => seen.Add((settings.ThemeMode, service.Current.ThemeMode));

        using var saved = new ManualResetEventSlim();
        _ = Task.Run(async () =>
        {
            await service.SaveAsync(AppSettings.Default with { ThemeMode = ThemeMode.Light });
            await service.SaveAsync(AppSettings.Default with { ThemeMode = ThemeMode.Dark });
            saved.Set();
        });

        // Blocking, not awaiting: both notifications must still be queued when
        // the drain below starts. Safe only because that path uses Post.
        Assert.True(saved.Wait(TimeSpan.FromSeconds(30)), "The background saves never completed.");
        Assert.Empty(seen);
        Assert.Equal(ThemeMode.Dark, service.Current.ThemeMode);

        Dispatcher.UIThread.RunJobs();

        // The first handler is handed Light even though Dark has already been
        // saved and is already what Current reports. A handler that applies its
        // argument applies settings the user has moved on from.
        Assert.Equal(ThemeMode.Light, seen[0].Delivered);
        Assert.Equal(ThemeMode.Dark, seen[0].Current);

        // Current is unambiguous at every delivery, which is what App relies on.
        Assert.All(seen, entry => Assert.Equal(ThemeMode.Dark, entry.Current));
    }
}
