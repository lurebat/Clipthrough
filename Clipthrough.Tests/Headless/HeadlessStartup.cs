using System;
using System.Threading;
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

    /// <summary>
    /// Makes <paramref name="scope"/> wait for <paramref name="viewModel"/>'s
    /// unawaited database work before deleting its directory: the latest
    /// database start (a save from the welcome screen starts one) and the
    /// maintenance pass that a start or a save leaves running. Either outlived
    /// the test still opening the database, so the delete failed and the
    /// directory leaked (docs/solutions/test-temp-databases-leak-through-the-connection-pool.md).
    /// The work posts to the UI thread, so the wait pumps it; it runs from the
    /// scope's Dispose, after the view model and window are torn down, which
    /// is the order docs/solutions/headless-teardown-leaks-dispatcher-jobs.md requires.
    /// </summary>
    public static void DrainDatabaseWorkBeforeDelete(TemporaryDatabaseScope scope, MainWindowViewModel viewModel)
    {
        scope.BeforeDelete = () =>
        {
            // Bounded and silent: teardown must not fail the test that owned it.
            // The start assigns the maintenance task as it finishes, so both are
            // re-read on every pass.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while ((viewModel.DatabaseStartupTask is { IsCompleted: false } || viewModel.MaintenanceTask is { IsCompleted: false })
                && DateTime.UtcNow < deadline)
            {
                try { Dispatcher.UIThread.RunJobs(); } catch { /* test teardown */ }
                Thread.Sleep(10);
            }
        };
    }
}