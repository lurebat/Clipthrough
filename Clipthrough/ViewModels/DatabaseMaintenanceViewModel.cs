using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Controls;
using Clipthrough.Services.Search;
using Clipthrough.Services;
using ReactiveUI;

namespace Clipthrough.ViewModels;

/// <summary>
/// Owns database-maintenance interactions (integrity check, daily backups,
/// restore, and the open-logs/open-database-folder helpers), extracted from
/// <see cref="MainWindowViewModel"/>. Errors from the folder helpers are routed
/// back through the <c>reportError</c> callback so they still surface in the
/// shared status bar; backup/restore/integrity surface their own status.
/// </summary>
public sealed class DatabaseMaintenanceViewModel : ViewModelBase
{
    private readonly IDatabaseBackupService _databaseBackupService;
    private readonly IStorageOptionsService _storageOptionsService;
    private readonly Database.SqliteConnectionFactory _connectionFactory;
    private readonly ISystemInteractionService _systemInteractionService;
    private readonly IAppNotificationService _notificationService;
    private readonly IClipboardMonitorService _clipboardMonitorService;
    private readonly IBackgroundOcrQueue _backgroundOcrQueue;
    private readonly IEmbeddingWorker? _embeddingWorker;
    private readonly Action<string, Exception> _reportError;

    public DatabaseMaintenanceViewModel(
        IDatabaseBackupService databaseBackupService,
        IStorageOptionsService storageOptionsService,
        ISystemInteractionService systemInteractionService,
        IAppNotificationService notificationService,
        IClipboardMonitorService clipboardMonitorService,
        IBackgroundOcrQueue backgroundOcrQueue,
        IEmbeddingWorker? embeddingWorker,
        Action<string, Exception> reportError)
    {
        _databaseBackupService = databaseBackupService;
        _storageOptionsService = storageOptionsService;
        // Built here rather than injected: it is a stateless wrapper over the
        // service above, and going through it keeps the connection string,
        // password and busy_timeout in one place instead of a hand-rolled copy
        // that omits whichever part its author forgot.
        _connectionFactory = new Database.SqliteConnectionFactory(storageOptionsService);
        _systemInteractionService = systemInteractionService;
        _notificationService = notificationService;
        _clipboardMonitorService = clipboardMonitorService;
        _backgroundOcrQueue = backgroundOcrQueue;
        _embeddingWorker = embeddingWorker;
        _reportError = reportError;

        OpenLogsFolderCommand = ReactiveCommand.CreateFromTask(OpenLogsFolderAsync);
        OpenDatabaseFolderCommand = ReactiveCommand.CreateFromTask(OpenDatabaseFolderAsync);
        RunIntegrityCheckCommand = ReactiveCommand.CreateFromTask(RunIntegrityCheckAsync);
        RefreshBackupsCommand = ReactiveCommand.Create(RefreshBackups);
        RestoreBackupCommand = ReactiveCommand.CreateFromTask<Window?>(RestoreBackupAsync);
        ObserveCommandErrors();
    }

    public ReactiveCommand<Unit, Unit> OpenLogsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDatabaseFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> RunIntegrityCheckCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshBackupsCommand { get; }
    public ReactiveCommand<Window?, Unit> RestoreBackupCommand { get; }

    public ObservableCollection<DatabaseBackupItem> Backups { get; } = new();

    private DatabaseBackupItem? _selectedBackup;
    public DatabaseBackupItem? SelectedBackup
    {
        get => _selectedBackup;
        set => this.RaiseAndSetIfChanged(ref _selectedBackup, value);
    }

    private string _integrityCheckStatus = string.Empty;
    public string IntegrityCheckStatus
    {
        get => _integrityCheckStatus;
        private set => this.RaiseAndSetIfChanged(ref _integrityCheckStatus, value);
    }

    private string _backupRestoreStatus = string.Empty;
    public string BackupRestoreStatus
    {
        get => _backupRestoreStatus;
        private set => this.RaiseAndSetIfChanged(ref _backupRestoreStatus, value);
    }

    /// <summary>Take the daily backup (called fire-and-forget at startup).</summary>
    public Task EnsureDailyBackupAsync() => _databaseBackupService.EnsureDailyBackupAsync();

    private async Task OpenLogsFolderAsync()
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(Diagnostics.TraceConfiguration.LogFilePath);
            if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            {
                await _systemInteractionService.OpenPathAsync(folder);
            }
        }
        catch (Exception ex)
        {
            _reportError("Open logs folder", ex);
        }
    }

    private async Task OpenDatabaseFolderAsync()
    {
        try
        {
            var dbPath = _storageOptionsService.Current.DatabasePath;
            var folder = System.IO.Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            {
                await _systemInteractionService.OpenPathAsync(folder);
            }
        }
        catch (Exception ex)
        {
            _reportError("Open database folder", ex);
        }
    }

    private async Task RunIntegrityCheckAsync()
    {
        IntegrityCheckStatus = "Running…";
        try
        {
            var problems = await Task.Run(() =>
            {
                var found = new System.Collections.Generic.List<string>();
                using var connection = _connectionFactory.CreateReadOnlyConnection();
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = reader.GetString(0);
                    if (!string.Equals(row, "ok", StringComparison.Ordinal))
                    {
                        found.Add(row);
                    }
                }
                return found;
            });

            if (problems.Count == 0)
            {
                IntegrityCheckStatus = "Integrity OK";
            }
            else
            {
                var summary = string.Join("; ", problems.Take(3));
                if (problems.Count > 3)
                {
                    summary += $" (+{problems.Count - 3} more)";
                }
                IntegrityCheckStatus = $"Problems found: {summary}";
                System.Diagnostics.Trace.TraceError($"Integrity check found problems: {string.Join("; ", problems)}");
            }
        }
        catch (Exception ex)
        {
            IntegrityCheckStatus = $"Check failed: {ex.Message}";
            System.Diagnostics.Trace.TraceError($"Integrity check failed: {ex}");
        }
    }

    public void RefreshBackups()
    {
        try
        {
            Backups.Clear();
            foreach (var info in _databaseBackupService.ListBackups())
            {
                Backups.Add(new DatabaseBackupItem(info));
            }
            BackupRestoreStatus = Backups.Count switch
            {
                0 => "No backups yet (one is created per day).",
                1 => "1 backup available",
                _ => $"{Backups.Count} backups available",
            };
        }
        catch (Exception ex)
        {
            BackupRestoreStatus = $"Listing failed: {ex.Message}";
            System.Diagnostics.Trace.TraceError($"List backups failed: {ex}");
        }
    }

    private async Task RestoreBackupAsync(Window? owner)
    {
        var target = SelectedBackup;
        if (target is null)
        {
            return;
        }

        var confirmed = owner is null
            ? true
            : await Clipthrough.Views.ConfirmDialog.ShowAsync(
                owner,
                "Restore from backup?",
                $"Replace the current database with the snapshot from {target.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm}?\n\nThe current database will be renamed with a .before-restore-* suffix so the swap is reversible. The application will exit afterwards; please restart it to load the restored data.",
                "Restore",
                "Cancel");
        if (!confirmed)
        {
            return;
        }

        try
        {
            BackupRestoreStatus = "Restoring…";

            // Quiescing the workers is deliberately NOT done here.
            // DatabaseBackupService.RestoreAsync enters a DatabaseMaintenanceScope
            // before it touches a file, and that scope stops the three workers,
            // clears the connection pools and restarts on dispose whatever it
            // found running - including when the restore throws.
            //
            // This method used to do all of that by hand first, which had two
            // costs. The duplicate protocol had to stay correct in two places,
            // and worse, it made the real one inert: the scope snapshots
            // IsRunning in its constructor, found all three already false, and
            // so stopped nothing and restarted nothing. Anyone reading
            // RestoreAsync saw an await using and concluded the workers were
            // quiesced by it; they were quiesced somewhere else entirely.
            // The hand-rolled version also never cleared the pools.
            // (arch-opus A26 and A27)
            await _databaseBackupService.RestoreAsync(target.Path);

            BackupRestoreStatus = "Restored. The app will exit — restart to load the restored data.";
            _notificationService.PublishInfo("Database restored", "Restart Clipthrough to load the restored snapshot.");

            // Defer the shutdown so the user sees the status update first.
            _ = Task.Delay(TimeSpan.FromMilliseconds(800)).ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime
                        is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown(0);
                    }
                }));
        }
        catch (Exception ex)
        {
            // No worker restart here. The scope inside RestoreAsync restarts on
            // dispose whatever it found running, including on the throwing path,
            // which is the case DatabaseMaintenanceScope was fixed for in
            // round 1 (K4). Restarting again from here would start workers this
            // method never stopped - and if the failure happened before the
            // scope was entered, there is nothing to restart at all.
            BackupRestoreStatus = $"Restore failed: {ex.Message}";
            System.Diagnostics.Trace.TraceError($"Restore from backup failed: {ex}");
            _notificationService.PublishError("Restore failed", ex.Message);
        }
    }
}
