using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The session-log view model is built with the main window and stays subscribed to every
/// trace line for the whole session, whether or not anyone opens the log window. That makes
/// its per-entry cost a cost the whole app pays.
/// </summary>
public sealed class SessionLogsViewModelHeadlessTests
{
    [AvaloniaFact]
    public void AddingEntries_WhileClosed_DoesNotTouchTheBoundCollection()
    {
        var service = new TestSessionLogService();
        using var viewModel = new SessionLogsViewModel(service);

        var notifications = 0;
        ((INotifyCollectionChanged)viewModel.VisibleSessionLogs).CollectionChanged += (_, _) => notifications++;

        for (var i = 0; i < 50; i++)
        {
            service.Emit(NewEntry($"line {i}"));
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, notifications);
        Assert.Empty(viewModel.VisibleSessionLogs);

        // Closed only suppresses the bound projection - the history is still there, and
        // opening the window has to show it.
        viewModel.Open();
        Assert.Equal(50, viewModel.VisibleSessionLogs.Count);
    }

    [AvaloniaFact]
    public void AddingAnEntry_WhileOpen_InsertsItRatherThanRebuildingTheList()
    {
        var service = new TestSessionLogService();
        using var viewModel = new SessionLogsViewModel(service);

        for (var i = 0; i < 200; i++)
        {
            service.Emit(NewEntry($"seed {i}"));
        }

        Dispatcher.UIThread.RunJobs();
        viewModel.Open();
        Assert.Equal(200, viewModel.VisibleSessionLogs.Count);

        var events = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)viewModel.VisibleSessionLogs).CollectionChanged += (_, e) => events.Add(e.Action);

        service.Emit(NewEntry("newest"));
        Dispatcher.UIThread.RunJobs();

        // A rebuild would be one Reset plus 201 Adds. Rebuilding on every line is what made
        // a session that logged n lines cost O(n^2) in change notifications alone.
        Assert.Equal([NotifyCollectionChangedAction.Add], events);
        Assert.Equal(201, viewModel.VisibleSessionLogs.Count);
        Assert.Equal("newest", viewModel.VisibleSessionLogs[0].Message);
    }

    [AvaloniaFact]
    public void AnEntryFilteredOut_IsNotShown_ButStillCounts()
    {
        var service = new TestSessionLogService();
        using var viewModel = new SessionLogsViewModel(service);
        viewModel.Open();

        viewModel.SelectedLogLevelOption = viewModel.LogLevelOptions.Single(o => o.Value == AppNotificationLevel.Error);

        service.Emit(NewEntry("a warning", AppNotificationLevel.Warning));
        service.Emit(NewEntry("an error", AppNotificationLevel.Error));
        Dispatcher.UIThread.RunJobs();

        var shown = Assert.Single(viewModel.VisibleSessionLogs);
        Assert.Equal("an error", shown.Message);

        // The incremental insert has to apply the same filter the rebuild does, or the two
        // paths disagree about what is visible.
        viewModel.SelectedLogLevelOption = viewModel.LogLevelOptions.Single(o => o.Value is null);
        Assert.Equal(2, viewModel.VisibleSessionLogs.Count);
    }

    [AvaloniaFact]
    public void TheInMemoryLog_IsBounded()
    {
        var service = new TestSessionLogService();
        using var viewModel = new SessionLogsViewModel(service);
        viewModel.Open();

        const int overflow = 25;
        for (var i = 0; i < SessionLogService.MaxRetainedEntries + overflow; i++)
        {
            service.Emit(NewEntry($"line {i}"));
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SessionLogService.MaxRetainedEntries, viewModel.VisibleSessionLogs.Count);

        // The bound collection has its own trim, so it stays capped even if the backing
        // list does not. CountText reads the backing list, which is the one that keeps
        // accumulating while the window is closed - the actual leak.
        Assert.Equal(AppText.FormatLogCount(SessionLogService.MaxRetainedEntries), viewModel.CountText);

        // Newest first, so the overflow has to come off the old end.
        Assert.Equal(
            $"line {SessionLogService.MaxRetainedEntries + overflow - 1}",
            viewModel.VisibleSessionLogs[0].Message);
    }

    [AvaloniaFact]
    public void TheServiceBuffer_IsBounded_AndKeepsTheNewestEntries()
    {
        // The singleton is wired into Trace for the whole process, so drive a private
        // instance instead of polluting it.
        var service = (SessionLogService)Activator.CreateInstance(typeof(SessionLogService), nonPublic: true)!;
        var write = typeof(SessionLogService).GetMethod(
            "AddEntry",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        const int overflow = 10;
        for (var i = 0; i < SessionLogService.MaxRetainedEntries + overflow; i++)
        {
            write.Invoke(service, [AppNotificationLevel.Warning, $"line {i}"]);
        }

        var snapshot = service.Snapshot();

        Assert.Equal(SessionLogService.MaxRetainedEntries, snapshot.Count);
        Assert.Equal($"line {overflow}", snapshot[0].Message);
        Assert.Equal($"line {SessionLogService.MaxRetainedEntries + overflow - 1}", snapshot[^1].Message);
    }

    [AvaloniaFact]
    public void TheService_PublishesOnTheThreadThatLogged()
    {
        var service = (SessionLogService)Activator.CreateInstance(typeof(SessionLogService), nonPublic: true)!;
        var write = typeof(SessionLogService).GetMethod(
            "AddEntry",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        SessionLogEntry? received = null;
        using var subscription = service.Entries.Subscribe(e => received = e);

        // No dispatcher pump between the write and the assertion: marshalling to the UI
        // thread here would wake the dispatcher once per trace line, for every line every
        // background worker writes, whether or not anything is listening.
        write.Invoke(service, [AppNotificationLevel.Error, "from this thread"]);

        Assert.NotNull(received);
        Assert.Equal("from this thread", received!.Message);
    }

    private static SessionLogEntry NewEntry(string message, AppNotificationLevel level = AppNotificationLevel.Warning)
        => new()
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Message = message,
        };
}
