using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Rubber-duck F-09-3, click part (S40/OD-9b). Search history records a query
/// when the user picks a result, and clicking a result is a pick. Selection
/// that the app makes on its own - a refresh, the auto-select of the first
/// row, a programmatic selection - is not, so only a click in the list
/// commits the query.
/// </summary>
public sealed class SearchHistoryClickHeadlessTests
{
    private const string Query = "invoice 2024";

    private static async Task<MainWindowTestHarness> SearchedAsync()
    {
        var harness = MainWindowTestHarness.Create();
        foreach (var text in new[] { "invoice 2024 for the quarter", "invoice 2024 for the year" })
        {
            await harness.ClipStore.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = text,
                ContentBytes = Encoding.UTF8.GetBytes(text),
                SourceApp = "Tests",
            });
        }

        harness.Settings.SetHasSavedSettings(true);
        harness.StorageOptions.SetHasSavedConfig(true);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.SetMainWindowVisible(true);
        for (var i = 0; i < 100 && harness.ViewModel.IsLoadingDatabase; i++)
        {
            await PumpAsync(TimeSpan.FromMilliseconds(20));
        }

        await PumpAsync(TimeSpan.FromMilliseconds(600));

        harness.ViewModel.SearchText = Query;
        // The search box refreshes 300 ms after the last keystroke.
        await PumpAsync(TimeSpan.FromMilliseconds(600));

        Assert.Equal(2, harness.ViewModel.Clips.Count(c => c.Clip.Content.Contains(Query, StringComparison.Ordinal)));
        return harness;
    }

    private static async Task PumpAsync(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    // The commit is fire-and-forget, so wait for the row rather than a fixed delay.
    private static async Task<IReadOnlyList<string>> WaitForHistoryAsync(MainWindowTestHarness harness)
    {
        var recorded = await harness.SearchHistory.GetRecentSearchesAsync();
        for (var i = 0; i < 200 && recorded.Count == 0; i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            recorded = await harness.SearchHistory.GetRecentSearchesAsync();
        }

        return recorded;
    }

    private static void ClickRow(MainWindowTestHarness harness, int index)
    {
        harness.ClipList.ScrollIntoView(index);
        Dispatcher.UIThread.RunJobs();
        var row = Assert.IsType<ListBoxItem>(harness.ClipList.ContainerFromIndex(index));
        var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), harness.Window);
        Assert.NotNull(centre);

        harness.Window.MouseDown(centre!.Value, MouseButton.Left);
        harness.Window.MouseUp(centre.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Proves the click reached the row, so a red result is about recording.
        Assert.Same(harness.ViewModel.Clips[index], harness.ViewModel.SelectedClip);

        // And that it stayed a click. Two clicks on one row are a double-tap,
        // which pastes - and pasting records the query by itself.
        Assert.Null(harness.SystemInteraction.LastCopiedText);
        Assert.Equal(0, harness.SystemInteraction.SimulatedPasteCount);
    }

    [AvaloniaFact]
    public async Task ClickingAResult_RecordsTheQuery()
    {
        using var harness = await SearchedAsync();

        ClickRow(harness, 1);

        Assert.Equal([Query], await WaitForHistoryAsync(harness));
    }

    [AvaloniaFact]
    public async Task ClickingResultsTwice_RecordsTheQueryOnce()
    {
        using var harness = await SearchedAsync();

        // Alternating rows: the same row twice in a row would be a double-tap.
        ClickRow(harness, 1);
        ClickRow(harness, 0);
        ClickRow(harness, 1);

        Assert.Equal([Query], await WaitForHistoryAsync(harness));
    }

    [AvaloniaFact]
    public async Task AutomaticSelection_RecordsNothing()
    {
        using var harness = await SearchedAsync();

        // The search's own refresh has already auto-selected a row. Move the
        // selection programmatically and refresh again, as the app does after a
        // paste or a capture.
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[1];
        harness.ViewModel.SelectedClip = null;
        await harness.ViewModel.RefreshCommand.Execute().ToTask();
        await PumpAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(harness.ViewModel.SelectedClip);
        Assert.Empty(await harness.SearchHistory.GetRecentSearchesAsync());
    }
}
