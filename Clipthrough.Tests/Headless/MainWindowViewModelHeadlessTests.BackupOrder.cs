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

    private sealed class ObservingBackupService<T>(Func<Task<T>> observe) : IDatabaseBackupService
    {
        public TaskCompletionSource<T> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task EnsureDailyBackupAsync(CancellationToken cancellationToken = default)
            => Observed.TrySetResult(await observe());

        public IReadOnlyList<DatabaseBackupInfo> ListBackups() => Array.Empty<DatabaseBackupInfo>();

        public Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Starts the view model and returns what <paramref name="observe"/> saw
    /// when the startup backup ran. Background services - the later recovery
    /// pass, the second maintenance pass and the maintenance loop - are held
    /// at the clipboard monitor's Start until then, so only what startup
    /// ordered before the backup can have happened by the time it runs, not
    /// whatever won a race on a given machine (rubber-duck F-06-8).
    /// </summary>
    private static async Task<T> ObserveStartupBackupAsync<T>(TemporaryDatabaseScope scope, Func<Task<T>> observe)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TestClipboardMonitorService? monitor = null;
        var backgroundStartedFirst = false;
        var backup = new ObservingBackupService<T>(async () =>
        {
            var seen = await observe();
            backgroundStartedFirst = monitor!.IsRunning;
            release.TrySetResult();
            return seen;
        });
        monitor = new TestClipboardMonitorService { StartGate = release.Task };
        try
        {
            using var viewModel = CreateViewModel(scope, monitor, new TestSystemInteractionService(), new TestSessionLogService(), databaseBackupService: backup);
            await viewModel.InitializeAsync();
            viewModel.SetMainWindowVisible(true);
            for (var i = 0; i < 400 && !backup.Observed.Task.IsCompleted; i++)
            {
                await Task.Delay(25);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }

            Assert.True(backup.Observed.Task.IsCompleted, "The startup backup never ran.");
            Assert.False(backgroundStartedFirst, "Background services started before the backup; the gate did not hold.");
            return await backup.Observed.Task;
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// Rubber-duck F-06-5. A clip captured just before a crash keeps
    /// is_sensitive = 0 with its scan pending, and the backup purge only removes
    /// rows flagged sensitive. Startup must classify such clips before the
    /// daily backup copies them, not merely at some point during startup.
    /// </summary>
    [AvaloniaFact]
    public async Task StartupClassifiesClipsWhoseScanNeverRan_BeforeTakingTheDailyBackup()
    {
        const string SecretText = "password = NOT-A-REAL-CREDENTIAL";
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var settings = AppSettings.Default with { EnableSensitiveClipLifetime = true };
        scope.SettingsService.SetCurrent(settings);
        scope.SettingsService.SetCurrentOnInitialize(settings);

        // A fast capture defers classification; the app "crashed" before it ran.
        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = SecretText,
            ContentBytes = Encoding.UTF8.GetBytes(SecretText),
        });
        Assert.False(clip!.IsSensitive);

        var flagged = await ObserveStartupBackupAsync(scope, async () => (await scope.ClipStoreService.GetByIdAsync(clip.Id))?.IsSensitive == true);

        Assert.True(flagged, "The startup backup copied a secret whose sensitivity scan had never run.");
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