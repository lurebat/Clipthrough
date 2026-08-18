using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Stepping through the search box's history with Up and Down.
///
/// This had no test and no mutant, and it was broken in a way that is obvious
/// the moment anything asserts it: <c>RecentSearches</c> is ordered
/// most-recent-first, and the code added the key's delta straight to the index,
/// so Up walked toward the newest entry rather than away from it and hit the
/// boundary on the second press. Pressing Up gave the latest search and then
/// cleared the box, over and over. Any entry that was not at one end of the
/// history could not be reached at all.
/// </summary>
public sealed class SearchHistoryNavigationHeadlessTests
{
    private static MainWindowTestHarness WithHistory(params string[] entries)
    {
        var harness = MainWindowTestHarness.Create();
        harness.ViewModel.RecentSearches.Clear();
        foreach (var entry in entries)
        {
            harness.ViewModel.RecentSearches.Add(entry);
        }

        Dispatcher.UIThread.RunJobs();
        return harness;
    }

    private static async Task<string> PressAsync(MainWindowTestHarness harness, int delta)
    {
        await harness.ViewModel.NavigateSearchHistoryAsync(delta);
        Dispatcher.UIThread.RunJobs();
        return harness.ViewModel.SearchText;
    }

    private const int Up = -1;
    private const int Down = 1;

    /// <summary>
    /// The one that was broken: everything between the ends has to be reachable.
    /// </summary>
    [AvaloniaFact]
    public async Task UpWalksBackThroughTheWholeHistory()
    {
        using var harness = WithHistory("newest", "middle", "oldest");

        Assert.Equal("newest", await PressAsync(harness, Up));
        Assert.Equal("middle", await PressAsync(harness, Up));
        Assert.Equal("oldest", await PressAsync(harness, Up));
    }

    /// <summary>
    /// And stays there. Clearing the box on reaching the end would throw away
    /// the oldest search the moment the key repeated onto it.
    /// </summary>
    [AvaloniaFact]
    public async Task UpStopsAtTheOldestRatherThanClearing()
    {
        using var harness = WithHistory("newest", "middle", "oldest");

        await PressAsync(harness, Up);
        await PressAsync(harness, Up);
        Assert.Equal("oldest", await PressAsync(harness, Up));

        Assert.Equal("oldest", await PressAsync(harness, Up));
        Assert.Equal("oldest", await PressAsync(harness, Up));
    }

    [AvaloniaFact]
    public async Task DownWalksForwardAgainAndThenEmptiesTheBox()
    {
        using var harness = WithHistory("newest", "middle", "oldest");

        await PressAsync(harness, Up);
        await PressAsync(harness, Up);
        Assert.Equal("oldest", await PressAsync(harness, Up));

        Assert.Equal("middle", await PressAsync(harness, Down));
        Assert.Equal("newest", await PressAsync(harness, Down));
        Assert.Equal(string.Empty, await PressAsync(harness, Down));
    }

    /// <summary>
    /// Down with nothing selected has nowhere newer to go, so it leaves the box
    /// as it found it rather than jumping to the far end of the history.
    /// </summary>
    [AvaloniaFact]
    public async Task DownFromAnUntouchedBoxDoesNothing()
    {
        using var harness = WithHistory("newest", "middle", "oldest");

        Assert.Equal(string.Empty, await PressAsync(harness, Down));
        Assert.Equal(string.Empty, await PressAsync(harness, Down));
    }

    [AvaloniaFact]
    public async Task AnEmptyHistoryLeavesTheBoxAlone()
    {
        using var harness = WithHistory();

        Assert.Equal(string.Empty, await PressAsync(harness, Up));
        Assert.Equal(string.Empty, await PressAsync(harness, Down));
    }

    /// <summary>
    /// A single entry behaves like the general case rather than being special:
    /// Up reaches it, Up again keeps it, Down returns to empty.
    /// </summary>
    [AvaloniaFact]
    public async Task AHistoryOfOneStillWalksAndReturns()
    {
        using var harness = WithHistory("only");

        Assert.Equal("only", await PressAsync(harness, Up));
        Assert.Equal("only", await PressAsync(harness, Up));
        Assert.Equal(string.Empty, await PressAsync(harness, Down));
    }
}
