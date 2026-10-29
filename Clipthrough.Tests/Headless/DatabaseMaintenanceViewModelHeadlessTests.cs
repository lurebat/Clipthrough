using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Search;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

public sealed class DatabaseMaintenanceViewModelHeadlessTests
{
    // Bug #5 originally: RestoreBackupAsync stopped the clipboard monitor, OCR
    // queue and embedding worker before the swap, and on FAILURE had to restart
    // them or the session went silently dead until the next launch.
    //
    // That protocol now lives where it belongs. DatabaseBackupService.RestoreAsync
    // enters a DatabaseMaintenanceScope before touching a file, and the scope
    // stops, clears the pools, and restarts on dispose whatever it found running
    // - including when the restore throws. Doing it here as well made the real
    // scope inert: it snapshots IsRunning in its constructor and found all three
    // already false. (round 2, arch-opus A26 and A27)
    //
    // So the property this test defends has changed rather than disappeared. The
    // view model must leave the workers alone; the restart guarantee is asserted
    // against the scope itself in DatabaseMaintenanceScopeTests, which is where
    // the code that provides it now lives.
    //
    // Since F-40 the view model is not even given the workers - it held them in
    // fields it never read, which invited exactly the hand-rolled quiesce A26/A27
    // removed - and IDE0052 at error stops them being parked there again. What is
    // left to assert here is that a failed restore is reported.
    [AvaloniaFact]
    public async Task RestoreBackup_WhenRestoreFails_ReportsTheFailure()
    {
        var vm = new DatabaseMaintenanceViewModel(
            new ThrowingBackupService(),
            new TestStorageOptionsService(@"C:\nonexistent\clipthrough.db"),
            new TestSystemInteractionService(),
            new TestNotificationService(),
            (_, _) => { });

        vm.SelectedBackup = new DatabaseBackupItem(
            new DatabaseBackupInfo(@"C:\some\backup.db", DateTimeOffset.UtcNow, 1024));

        // owner == null auto-confirms; the restore then throws inside the try.
        await vm.RestoreBackupCommand.Execute(null);
        Assert.Contains("Restore failed", vm.BackupRestoreStatus);
    }

    private sealed class ThrowingBackupService : IDatabaseBackupService
    {
        public Task EnsureDailyBackupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<DatabaseBackupInfo> ListBackups() => Array.Empty<DatabaseBackupInfo>();

        public Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated restore failure");
    }

}
