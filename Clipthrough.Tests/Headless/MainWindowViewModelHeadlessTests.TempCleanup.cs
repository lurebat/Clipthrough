using System;
using System.IO;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Per-test database directories are deleted even when the test leaves the
/// view model's database work running: a database start, a maintenance pass or
/// the daily backup
/// (docs/solutions/test-temp-databases-leak-through-the-connection-pool.md).
/// </summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    /// <summary>
    /// Saving from the welcome screen starts the database in the background and
    /// returns; nothing awaits it. Before the scope waited for it, the start was
    /// still reopening the database when the scope deleted the directory, so the
    /// delete failed and the directory leaked - five of them in one full
    /// headless run.
    /// </summary>
    [AvaloniaFact]
    public async Task ASaveFromTheWelcomeScreen_DoesNotLeaveItsDatabaseDirectoryBehind()
    {
        var scope = new TemporaryDatabaseScope();
        Task? startup;
        try
        {
            using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService());
            await viewModel.InitializeAsync();
            Assert.True(viewModel.IsWelcomeOpen, "The scope has no library, so the view model should start on the welcome screen.");

            await viewModel.SaveSettingsCommand.Execute().ToTask();
            startup = viewModel.DatabaseStartupTask;
            Assert.NotNull(startup);
        }
        finally
        {
            scope.Dispose();
        }

        Assert.True(startup.IsCompleted, "The scope deleted its directory while the database start was still running.");
        Assert.False(Directory.Exists(scope.DirectoryPath), $"The test database directory was left behind: {scope.DirectoryPath}");
    }

    /// <summary>
    /// A save with the database open ends by starting a maintenance pass that
    /// nothing awaits; it reopens the database to reload the rules and refresh
    /// the list. A test that disposed right after the save deleted the
    /// directory under it. On an empty library the pass usually finishes inside
    /// the delete's retries, which hid it; the store here holds each pass past
    /// them, so a scope that does not wait deletes while the pass is running.
    /// </summary>
    [AvaloniaFact]
    public async Task ASaveWithTheDatabaseOpen_DoesNotLeaveItsDatabaseDirectoryBehind()
    {
        var scope = new TemporaryDatabaseScope();
        Task? maintenance;
        try
        {
            await PrepareInitializedScopeAsync(scope);
            var store = DispatchProxy.Create<IClipStoreService, SlowMaintenanceStore>();
            ((SlowMaintenanceStore)(object)store).Inner = scope.ClipStoreService;
            using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService(), clipStore: store);
            await HeadlessStartup.InitializeAndWaitForDatabaseAsync(viewModel);
            Assert.False(viewModel.IsWelcomeOpen, "The scope is prepared, so the save should take the database-open path.");

            await viewModel.SaveSettingsCommand.Execute().ToTask();
            maintenance = viewModel.MaintenanceTask;
            Assert.NotNull(maintenance);
        }
        finally
        {
            scope.Dispose();
        }

        Assert.True(maintenance.IsCompleted, "The scope deleted its directory while the maintenance pass was still running.");
        Assert.False(Directory.Exists(scope.DirectoryPath), $"The test database directory was left behind: {scope.DirectoryPath}");
    }

    /// <summary>
    /// Opening the database also starts the daily backup on a pool thread, and
    /// nothing awaits it. It copies the library into the directory's backups
    /// folder; a test that disposed while it ran deleted the directory under
    /// it. The backup here waits past the delete's retries and records whether
    /// the directory was still there when it ran.
    /// </summary>
    [AvaloniaFact]
    public async Task TheStartupBackup_RunsBeforeTheScopeDeletesItsDirectory()
    {
        var scope = new TemporaryDatabaseScope();
        SlowBackupService backup;
        try
        {
            await PrepareInitializedScopeAsync(scope);
            backup = new SlowBackupService(
                scope.DirectoryPath,
                new DatabaseBackupService(scope.StorageOptionsService, null, null, null, settingsService: scope.SettingsService, databaseInitializer: scope.DatabaseInitializer));
            using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService(), databaseBackupService: backup);
            await HeadlessStartup.InitializeAndWaitForDatabaseAsync(viewModel);
        }
        finally
        {
            scope.Dispose();
        }

        var ran = await Task.WhenAny(backup.Ran.Task, Task.Delay(TimeSpan.FromSeconds(20))) == backup.Ran.Task;
        Assert.True(ran, "The startup backup never ran.");
        Assert.True(await backup.Ran.Task, "The startup backup ran after the scope had deleted its directory.");
        Assert.False(Directory.Exists(scope.DirectoryPath), $"The test database directory was left behind: {scope.DirectoryPath}");
    }

    /// <summary>Takes a real backup two seconds late, recording whether the directory still existed then.</summary>
    private sealed class SlowBackupService(string directoryPath, IDatabaseBackupService inner) : IDatabaseBackupService
    {
        public TaskCompletionSource<bool> Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task EnsureDailyBackupAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            var directoryExisted = Directory.Exists(directoryPath);
            try
            {
                if (directoryExisted)
                {
                    await inner.EnsureDailyBackupAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                Ran.TrySetResult(directoryExisted);
            }
        }

        public System.Collections.Generic.IReadOnlyList<DatabaseBackupInfo> ListBackups() => inner.ListBackups();

        public Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default) => inner.RestoreAsync(backupPath, cancellationToken);
    }

    /// <summary>Forwards to a real store, but each maintenance pass first waits two seconds.</summary>
    public class SlowMaintenanceStore : DispatchProxy
    {
        public IClipStoreService Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IClipStoreService.ApplyMaintenanceAsync))
            {
                return SlowMaintenanceAsync((CancellationToken)args![0]!);
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }
        }

        private async Task<ClipMaintenanceResult> SlowMaintenanceAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            return await Inner.ApplyMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
