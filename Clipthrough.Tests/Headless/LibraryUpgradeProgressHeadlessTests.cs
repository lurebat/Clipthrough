using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

    private static void SeedLegacyRows(TemporaryDatabaseScope scope)
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
            UPDATE app_metadata SET value = CAST(CAST(value AS INTEGER) - 1 AS TEXT) WHERE key = 'schema_version';
            DELETE FROM app_metadata WHERE key = 'payload_move_last_id';
            """;
        rewind.ExecuteNonQuery();
    }

    private static int? Percent(string? text)
    {
        var match = text is null ? Match.Empty : Regex.Match(text, @"(\d+)%");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    [AvaloniaFact]
    public async Task TheFirstStartAfterAnUpgrade_ShowsItsProgress_AndKeepsTheWindowLive()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);
        Task.Run(() => scope.DatabaseInitializer.InitializeAsync()).GetAwaiter().GetResult();
        SeedLegacyRows(scope);

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
                new TestClipSampleDataService(),
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
