using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Database;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// U-E1, owner decision (C): the first start after an upgrade shows what it is
/// doing and how far it has got, and the window stays live while it runs. That
/// start blocked for 87 s on the 1 GB fixture behind an indeterminate "Loading"
/// bar.
/// </summary>
/// <remarks>
/// The payload move is held mid-batch through <see cref="DatabaseInitializer.AfterPayloadRowMoved"/>
/// so the overlay can be inspected while the upgrade is genuinely in flight.
/// The seams are static; the assembly runs its tests one at a time, and both are
/// restored in <c>finally</c>.
/// </remarks>
public sealed class LibraryUpgradeProgressHeadlessTests
{
    private const int LegacyRows = 4;

    /// <param name="version">
    /// The schema the library claims. Below 8 the move still runs before the app
    /// opens, behind the overlay; from 8 it runs in the background (U4).
    /// </param>
    private static void SeedLegacyRows(TemporaryDatabaseScope scope, int version)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        for (var i = 0; i < LegacyRows; i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clips (content, content_bytes, content_type, content_format, source_app,
                    hash, captured_at, first_copied_at, last_copied_at, byte_size, sensitivity_scanned_at)
                VALUES ($c, $b, 'image', 'bitmap', 'Paint', $h, $t, $t, $t, 3, $t);
                """;
            command.Parameters.AddWithValue("$c", $"Image {i}");
            command.Parameters.AddWithValue("$b", new byte[] { (byte)i, 1, 2 });
            command.Parameters.AddWithValue("$h", $"legacy-{i}");
            command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddMinutes(i).ToString("O"));
            command.ExecuteNonQuery();
        }

        using var rewind = connection.CreateCommand();
        rewind.CommandText = """
            UPDATE app_metadata SET value = $v WHERE key = 'schema_version';
            DELETE FROM app_metadata WHERE key IN ('payload_move_last_id', 'payload_move_done');
            """;
        rewind.Parameters.AddWithValue("$v", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        rewind.ExecuteNonQuery();
    }

    private static int? Percent(string? text)
    {
        var match = text is null ? Match.Empty : Regex.Match(text, @"(\d+)%");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static string TodaysBackup(TemporaryDatabaseScope scope) => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(scope.DatabasePath)!,
        "backups",
        $"clipthrough-{DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}.db");

    [AvaloniaFact]
    public async Task TheFirstStartAfterAnUpgrade_ShowsItsProgress_AndKeepsTheWindowLive()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);
        Task.Run(() => scope.DatabaseInitializer.InitializeAsync()).GetAwaiter().GetResult();
        SeedLegacyRows(scope, version: 4);

        using var moveReached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        DatabaseInitializer.PayloadMoveBatchRows = 1;
        DatabaseInitializer.AfterPayloadRowMoved = _ =>
        {
            if (!moveReached.IsSet)
            {
                moveReached.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        MainWindow? window = null;
        MainWindowViewModel? viewModel = null;
        try
        {
            var systemInteraction = new TestSystemInteractionService();
            viewModel = new MainWindowViewModel(
                scope.ClipStoreService,
                new TestClipboardMonitorService(),
                scope.SettingsService,
                systemInteraction,
                scope.StorageOptionsService,
                scope.SensitivityService,
                scope.NotificationService,
                new TestSessionLogService(),
                scope.ClipExportService,
                new TestImageEditorService(),
                scope.SearchHistoryService,
                new TestAiTransformService(false),
                new TestOcrService(),
                new BackgroundOcrQueueStub(),
                new BackgroundJobIndicator(),
                scope.DatabaseInitializer);
            window = new MainWindow(systemInteraction) { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var stageText = window.FindControl<TextBlock>("LibraryUpgradeStageTextBlock");
            var bar = window.FindControl<ProgressBar>("LibraryUpgradeProgressBar");
            var title = window.FindControl<TextBlock>("LibraryUpgradeTitleTextBlock");
            Assert.NotNull(stageText);
            Assert.NotNull(bar);
            Assert.NotNull(title);

            // The bound controls cannot tell where a change was raised: since
            // Avalonia 12.1.4 a binding marshals a PropertyChanged from another
            // thread itself, so the overlay looks right either way. Every other
            // subscriber still runs on whatever thread raised it, so the thread is
            // asserted directly.
            var stageRaises = 0;
            var offThreadRaises = new List<string>();
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not (nameof(MainWindowViewModel.IsUpgradingLibrary)
                    or nameof(MainWindowViewModel.LibraryUpgradeStageText)
                    or nameof(MainWindowViewModel.LibraryUpgradeProgressValue)
                    or nameof(MainWindowViewModel.IsLibraryUpgradeProgressIndeterminate)))
                {
                    return;
                }

                lock (offThreadRaises)
                {
                    stageRaises++;
                    if (!Dispatcher.UIThread.CheckAccess())
                    {
                        offThreadRaises.Add($"{e.PropertyName} on thread {Environment.CurrentManagedThreadId}");
                    }
                }
            };

            var seen = new List<string>();
            using var subscription = stageText.GetObservable(TextBlock.TextProperty).Subscribe(t =>
            {
                if (!string.IsNullOrEmpty(t))
                {
                    seen.Add(t);
                }
            });

            // Returning at all while the move is held is the first half of
            // "responsive": InitializeAsync hands the upgrade to a worker and does
            // not wait for it on the UI thread.
            await viewModel.InitializeAsync();

            var clock = Stopwatch.StartNew();
            while (!moveReached.IsSet && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(moveReached.IsSet, "The payload move never started, so nothing below would be in flight.");
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            // The upgrade is held mid-batch on a worker, and this thread - the UI
            // thread - is running and has applied its progress.
            Assert.False(release.IsSet);
            Assert.True(viewModel.IsLoadingDatabase);
            Assert.True(title.IsEffectivelyVisible);
            Assert.Equal(AppText.LibraryUpgradeTitle, title.Text);
            Assert.True(bar.IsEffectivelyVisible);
            Assert.False(bar.IsIndeterminate, "The move knows how far it has got; the bar should say so.");
            Assert.StartsWith(AppText.LibraryUpgradeStageMovingClipData, stageText.Text);

            release.Set();
            clock.Restart();
            while (viewModel.IsLoadingDatabase && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.False(viewModel.IsLoadingDatabase);
            Assert.False(viewModel.IsUpgradingLibrary, "The upgrade state outlived the load; the next load would show it again.");
            Assert.False(bar.IsEffectivelyVisible);
            Assert.False(title.IsEffectivelyVisible);

            var movePercents = seen
                .Where(t => t.StartsWith(AppText.LibraryUpgradeStageMovingClipData, StringComparison.Ordinal))
                .Select(Percent)
                .ToList();
            Assert.All(movePercents, p => Assert.NotNull(p));
            Assert.Equal(100, movePercents.Last());
            Assert.Equal(movePercents.OrderBy(p => p), movePercents);
            Assert.True(movePercents.Distinct().Count() > 2, $"Expected the move to report its batches; saw {string.Join(", ", seen)}.");

            lock (offThreadRaises)
            {
                Assert.True(stageRaises > 2, $"Expected the upgrade to raise its progress properties; saw {stageRaises}.");
                Assert.True(offThreadRaises.Count == 0, $"Progress was applied off the UI thread: {string.Join("; ", offThreadRaises.Take(4))}");
            }
        }
        finally
        {
            release.Set();
            DatabaseInitializer.AfterPayloadRowMoved = null;
            DatabaseInitializer.PayloadMoveBatchRows = 200;

            try { window?.Close(); } catch { /* test teardown */ }
            viewModel?.Dispose();
            for (var i = 0; i < 5; i++)
            {
                try { Dispatcher.UIThread.RunJobs(); } catch { /* test teardown */ }
            }
        }
    }

    /// <summary>
    /// U4, owner decision (c): an upgrade from schema 8 opens at once and moves
    /// payloads in the background. With the move held between batches, the clip
    /// list has loaded, a capture lands, and the running-jobs line shows the move
    /// and its progress; once released, the move finishes and the line clears.
    /// </summary>
    [AvaloniaFact]
    public async Task AnUpgradeFromSchema8_OpensAtOnce_AndShowsTheBackgroundMove()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });
        Task.Run(() => scope.DatabaseInitializer.InitializeAsync()).GetAwaiter().GetResult();
        SeedLegacyRows(scope, version: 8);

        // As if a search-index rebuild had left merges behind: the background
        // work drains them after the move (R1).
        using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            connection.Open();
            using var pending = connection.CreateCommand();
            pending.CommandText = "INSERT INTO app_metadata (key, value) VALUES ('fts_merge_pending', '1') ON CONFLICT(key) DO UPDATE SET value = '1';";
            pending.ExecuteNonQuery();
        }

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        DatabaseInitializer.BackgroundPayloadMoveBatchBudget = TimeSpan.Zero;
        DatabaseInitializer.BetweenBackgroundPayloadBatches = _ =>
        {
            held.Set();
            return Task.Run(() => release.Wait(TimeSpan.FromSeconds(30)));
        };

        // Recorded as the indicator raises them, on the reporting thread. The
        // view model's label reads whatever is current when its posted update
        // runs, so a 0% replaced by 25% before the dispatcher turns is never seen
        // there.
        var jobs = new BackgroundJobIndicator();
        var seenJobLabels = new System.Collections.Concurrent.ConcurrentQueue<string>();
        jobs.Changed += (_, _) =>
        {
            foreach (var label in jobs.ActiveLabels)
            {
                seenJobLabels.Enqueue(label);
            }
        };
        MainWindow? window = null;
        MainWindowViewModel? viewModel = null;
        try
        {
            var systemInteraction = new TestSystemInteractionService();
            viewModel = new MainWindowViewModel(
                scope.ClipStoreService,
                new TestClipboardMonitorService(),
                scope.SettingsService,
                systemInteraction,
                scope.StorageOptionsService,
                scope.SensitivityService,
                scope.NotificationService,
                new TestSessionLogService(),
                scope.ClipExportService,
                new TestImageEditorService(),
                scope.SearchHistoryService,
                new TestAiTransformService(false),
                new TestOcrService(),
                new BackgroundOcrQueueStub(),
                jobs,
                scope.DatabaseInitializer);
            window = new MainWindow(systemInteraction) { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            await viewModel.InitializeAsync();
            var clock = Stopwatch.StartNew();
            while ((viewModel.IsLoadingDatabase || !held.IsSet) && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(held.IsSet, "The background move never started.");
            Assert.False(viewModel.IsLoadingDatabase, "The app did not open before the move finished.");
            Assert.False(viewModel.IsUpgradingLibrary, "The blocking upgrade overlay showed for a background move.");

            clock.Restart();
            while (viewModel.Clips.Count < LegacyRows && clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(LegacyRows, viewModel.Clips.Count);
            Assert.True(viewModel.HasRunningJobs);
            Assert.StartsWith(AppText.FormatLibraryOptimizeJob(0)[..^2], viewModel.RunningJobsLabel);

            // Held after the first of four one-row batches: the bar has moved off 0, and the 0% report before it showed too.
            Assert.Contains(seenJobLabels, l => l == AppText.FormatLibraryOptimizeJob(0));

            var captured = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = "copied while the library was being optimized",
                ContentBytes = System.Text.Encoding.UTF8.GetBytes("copied while the library was being optimized"),
            });
            Assert.NotNull(captured);

            // The startup backup waits for the background integrity check (live check F3).
            Assert.False(System.IO.File.Exists(TodaysBackup(scope)), "The daily backup ran before the background integrity check.");

            DatabaseInitializer.BetweenBackgroundPayloadBatches = null;
            release.Set();
            clock.Restart();
            while (viewModel.HasRunningJobs && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.False(viewModel.HasRunningJobs, "The move's job line outlived the move.");
            using var connection = scope.ConnectionFactory.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL;";
            Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            clock.Restart();
            long pendingMerges;
            do
            {
                command.CommandText = "SELECT COUNT(*) FROM app_metadata WHERE key = 'fts_merge_pending';";
                pendingMerges = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                if (pendingMerges > 0)
                {
                    await Task.Delay(20);
                }
            }
            while (pendingMerges > 0 && clock.Elapsed < TimeSpan.FromSeconds(10));
            Assert.Equal(0L, pendingMerges);

            // ...and is taken once the run has finished.
            clock.Restart();
            while (!System.IO.File.Exists(TodaysBackup(scope)) && clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(System.IO.File.Exists(TodaysBackup(scope)), "No daily backup after the background run passed its check.");
        }
        finally
        {
            release.Set();
            DatabaseInitializer.BetweenBackgroundPayloadBatches = null;
            DatabaseInitializer.BackgroundPayloadMoveBatchBudget = TimeSpan.FromMilliseconds(100);

            try { window?.Close(); } catch { /* test teardown */ }
            viewModel?.Dispose();
            for (var i = 0; i < 5; i++)
            {
                try { Dispatcher.UIThread.RunJobs(); } catch { /* test teardown */ }
            }
        }
    }

    /// <summary>
    /// Owner decision on the integrity check (b): the background move checks the
    /// library before rewriting it. On a corrupt library the app is open, nothing
    /// is moved, and the failure is raised as an error notification.
    /// </summary>
    [AvaloniaFact]
    public async Task ACorruptLibrary_IsNotMovedInTheBackground_AndTheFailureIsReported()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);
        Task.Run(() => scope.DatabaseInitializer.InitializeAsync()).GetAwaiter().GetResult();
        SeedLegacyRows(scope, version: 8);
        using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            connection.Open();
            using var fill = connection.CreateCommand();
            fill.CommandText = """
                CREATE TABLE integrity_test_filler (id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 3000)
                INSERT INTO integrity_test_filler (payload) SELECT printf('%.400c', 'x') FROM n;
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
            fill.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using (var file = new System.IO.FileStream(scope.DatabasePath, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite))
        {
            file.Seek(file.Length / 2, System.IO.SeekOrigin.Begin);
            var garbage = new byte[256];
            Array.Fill(garbage, (byte)0xDE);
            file.Write(garbage);
        }

        MainWindowViewModel? viewModel = null;
        try
        {
            viewModel = new MainWindowViewModel(
                scope.ClipStoreService,
                new TestClipboardMonitorService(),
                scope.SettingsService,
                new TestSystemInteractionService(),
                scope.StorageOptionsService,
                scope.SensitivityService,
                scope.NotificationService,
                new TestSessionLogService(),
                scope.ClipExportService,
                new TestImageEditorService(),
                scope.SearchHistoryService,
                new TestAiTransformService(false),
                new TestOcrService(),
                new BackgroundOcrQueueStub(),
                new BackgroundJobIndicator(),
                scope.DatabaseInitializer);

            await viewModel.InitializeAsync();
            // On the view model's list, not the service's last publish: the publish
            // happens on the background run's thread and reaches the list through a
            // posted UI job, which may still be queued when the service shows it.
            var clock = Stopwatch.StartNew();
            while (!viewModel.Notifications.Any(n => n.Title == AppText.LibraryOptimizeFailedTitle) && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.False(viewModel.IsLoadingDatabase, "The app did not open.");
            var notification = scope.NotificationService.LastNotification;
            Assert.NotNull(notification);
            Assert.Equal(AppText.LibraryOptimizeFailedTitle, notification.Title);
            Assert.Equal(AppNotificationLevel.Error, notification.Level);
            Assert.Contains("integrity check failed", notification.Message, StringComparison.OrdinalIgnoreCase);

            // Owner decision (live check F3): this one stays until the user closes
            // it. A non-persistent notification is removed after six seconds - long
            // before someone looking away from the screen reads "restore a backup".
            Assert.True(notification.IsPersistent, "The integrity-failure notification disappears on its own.");
            var shown = Assert.Single(viewModel.Notifications, n => n.Title == AppText.LibraryOptimizeFailedTitle);
            var close = Assert.Single(shown.Actions);
            Assert.Equal(AppText.CloseButtonLabel, close.Label);
            await close.ExecuteCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(viewModel.Notifications, n => n.Title == AppText.LibraryOptimizeFailedTitle);

            using var connection = scope.ConnectionFactory.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL), (SELECT COUNT(*) FROM clip_payloads);";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(LegacyRows, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));

            // Live check F3: nothing is backed up from a library that failed its check.
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.False(System.IO.File.Exists(TodaysBackup(scope)), "A daily backup was taken from a library that failed its integrity check.");
        }
        finally
        {
            viewModel?.Dispose();
            for (var i = 0; i < 5; i++)
            {
                try { Dispatcher.UIThread.RunJobs(); } catch { /* test teardown */ }
            }
        }
    }
    private sealed class BackgroundOcrQueueStub : IBackgroundOcrQueue
    {
        public IObservable<long> OcrCompleted { get; } = System.Reactive.Linq.Observable.Empty<long>();
        public IObservable<System.Reactive.Unit> QueueChanged { get; } = System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();
        public bool IsRunning { get; private set; }
        public void Start() { IsRunning = true; }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public void Enqueue(long clipId) { }
        public Task EnqueueBacklogAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
