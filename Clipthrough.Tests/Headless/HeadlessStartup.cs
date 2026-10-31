using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clipthrough.ViewModels;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// F-30. <see cref="MainWindowViewModel.InitializeAsync"/> starts opening the
/// database on a worker thread and returns. Until it lands, a refresh is a
/// no-op, so a test that captured or deleted straight after InitializeAsync was
/// racing the startup it never waited for: a 1.5 s delay before the database
/// is marked ready failed 11 of 65 view-model tests, including the baseline
/// flake ("Expected: Not 42, Actual: 42"). Every headless test starts the view
/// model through here.
/// </summary>
internal static class HeadlessStartup
{
    public static async Task InitializeAndWaitForDatabaseAsync(MainWindowViewModel viewModel, TimeSpan? timeout = null)
    {
        await viewModel.InitializeAsync();

        // The whole background start, not just the loading flag: the flag drops
        // before the clipboard monitor is wired, so a capture emitted in between
        // was silently missed.
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (viewModel.IsLoadingDatabase || viewModel.DatabaseStartupTask is { IsCompleted: false })
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The view model's database did not finish opening.");
            }

            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }
}