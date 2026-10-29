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
}