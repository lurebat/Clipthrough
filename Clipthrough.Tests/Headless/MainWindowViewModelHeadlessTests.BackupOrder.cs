using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>F-19 / OD-4a, ordering half: expiry runs before the startup backup.</summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    private sealed class ObservingBackupService(Func<Task<bool>> isExpiredClipStillLive) : IDatabaseBackupService
    {
        public TaskCompletionSource<bool> ExpiredClipLiveAtBackup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task EnsureDailyBackupAsync(CancellationToken cancellationToken = default)
            => ExpiredClipLiveAtBackup.TrySetResult(await isExpiredClipStillLive());

        public IReadOnlyList<DatabaseBackupInfo> ListBackups() => Array.Empty<DatabaseBackupInfo>();

        public Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task StartupExpiresSensitiveClips_BeforeTakingTheDailyBackup()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var settings = AppSettings.Default with
        {
            EnableSensitiveClipLifetime = true,
            SensitiveClipLifetimeMinutes = 5,
        };
        scope.SettingsService.SetCurrent(settings);
        scope.SettingsService.SetCurrentOnInitialize(settings);
        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "NOT-A-REAL-SECRET",
            ContentBytes = Encoding.UTF8.GetBytes("NOT-A-REAL-SECRET"),
            SkipPostInsertMaintenance = true,
        });
        await scope.ClipStoreService.SetSensitiveAsync(clip!.Id, true);
        using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE clips SET captured_at = $t, last_copied_at = $t;";
            command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddHours(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        var backup = new ObservingBackupService(async () => await scope.ClipStoreService.GetByIdAsync(clip.Id) is not null);
        using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService(), databaseBackupService: backup);
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        for (var i = 0; i < 400 && !backup.ExpiredClipLiveAtBackup.Task.IsCompleted; i++)
        {
            await Task.Delay(25);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.True(backup.ExpiredClipLiveAtBackup.Task.IsCompleted, "The startup backup never ran.");
        var observed = await backup.ExpiredClipLiveAtBackup.Task;

        Assert.False(observed, "The startup backup ran while an expired sensitive clip was still in the library.");
    }

    /// <summary>
    /// Holds the first daily backup it is asked for, and counts every request,
    /// so a second request made while the first is still copying shows.
    /// </summary>
    private sealed class HoldingBackupService : IDatabaseBackupService
    {
        private int _calls;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public Task EnsureDailyBackupAsync(CancellationToken cancellationToken = default)
            => Interlocked.Increment(ref _calls) == 1 ? Release.Task : Task.CompletedTask;

        public IReadOnlyList<DatabaseBackupInfo> ListBackups() => Array.Empty<DatabaseBackupInfo>();

        public Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Rubber-duck F-13-8: the background upgrade run ends with a maintenance pass
    /// and the daily backup, for the backup a deferred start held back until its
    /// integrity check. It did that on every start, including the ordinary ones
    /// with nothing to move, so each start ran maintenance twice and two backups
    /// at once onto the same temporary file.
    /// </summary>
    [AvaloniaFact]
    public async Task AnOrdinaryStart_TakesTheDailyBackupOnce()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var backup = new HoldingBackupService();
        using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService(), databaseBackupService: backup);
        try
        {
            await viewModel.InitializeAsync();
            viewModel.SetMainWindowVisible(true);
            for (var i = 0; i < 800 && (backup.Calls == 0 || viewModel.DeferredUpgradeRun is null); i++)
            {
                await Task.Delay(25);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }

            Assert.True(backup.Calls > 0, "The startup backup never ran.");
            Assert.NotNull(viewModel.DeferredUpgradeRun);

            // The first request is held. If the background run asks again it
            // either gets through as a second request or, if it asked first,
            // never finishes.
            var finished = await Task.WhenAny(viewModel.DeferredUpgradeRun, Task.Delay(TimeSpan.FromSeconds(20))) == viewModel.DeferredUpgradeRun;
            Assert.True(finished, "The background upgrade run is waiting on a daily backup on an ordinary start.");
            Assert.True(backup.Calls == 1, $"An ordinary start asked for the daily backup {backup.Calls} times.");
        }
        finally
        {
            backup.Release.TrySetResult();
        }
    }
}