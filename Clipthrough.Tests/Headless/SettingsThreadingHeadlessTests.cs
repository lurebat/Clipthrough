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
}
