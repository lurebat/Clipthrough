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
    [AvaloniaFact]
    public async Task RestoreBackup_WhenRestoreFails_LeavesWorkerLifecycleToTheScope()
    {
        var monitor = new RecordingMonitor();
        var ocr = new RecordingOcrQueue();
        var embedding = new RecordingEmbeddingWorker();

        var vm = new DatabaseMaintenanceViewModel(
            new ThrowingBackupService(),
            new TestStorageOptionsService(@"C:\nonexistent\clipthrough.db"),
            new TestSystemInteractionService(),
            new TestNotificationService(),
            monitor,
            ocr,
            embedding,
            (_, _) => { });

        vm.SelectedBackup = new DatabaseBackupItem(
            new DatabaseBackupInfo(@"C:\some\backup.db", DateTimeOffset.UtcNow, 1024));

        // owner == null auto-confirms; the restore then throws inside the try.
        await vm.RestoreBackupCommand.Execute(null);

        Assert.Equal(0, monitor.StopCount);
        Assert.Equal(0, monitor.StopAsyncCount);

        // And having stopped nothing, it must not start anything either: a
        // restart from here would run workers this method never quiesced, and
        // would fight the scope over which of them should be running.
        Assert.Equal(0, monitor.StartCount);
        Assert.Equal(0, ocr.StartCount);
        Assert.Equal(0, embedding.StartCount);

        Assert.Contains("Restore failed", vm.BackupRestoreStatus);
    }

    private sealed class ThrowingBackupService : IDatabaseBackupService
    {
        public Task EnsureDailyBackupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<DatabaseBackupInfo> ListBackups() => Array.Empty<DatabaseBackupInfo>();

        public Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated restore failure");
    }

    private sealed class RecordingMonitor : IClipboardMonitorService
    {
        public int StartCount;
        public int StopCount;
        public bool IsRunning { get; private set; } = true;

        public IObservable<ClipEntry> CapturedClips => Observable.Empty<ClipEntry>();
        public IObservable<ClipEntry> UpdatedClips => Observable.Empty<ClipEntry>();
        public IObservable<bool> CaptureBusy => Observable.Empty<bool>();

        public void Start() { StartCount++; IsRunning = true; }
        public void Stop() { StopCount++; IsRunning = false; }
        public int StopAsyncCount;
        public Task StopAsync() { StopAsyncCount++; IsRunning = false; return Task.CompletedTask; }
        public void SuppressNext() { }

        public void CancelSuppressNext() { }
    }

    private sealed class RecordingOcrQueue : IBackgroundOcrQueue
    {
        public int StartCount;
        public bool IsRunning { get; private set; } = true;

        public IObservable<long> OcrCompleted => Observable.Empty<long>();
        public IObservable<System.Reactive.Unit> QueueChanged => Observable.Empty<System.Reactive.Unit>();

        public void Start() { StartCount++; IsRunning = true; }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public void Enqueue(long clipId) { }
        public Task EnqueueBacklogAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingEmbeddingWorker : IEmbeddingWorker
    {
        public int StartCount;
        public bool IsRunning { get; private set; } = true;

        public IObservable<int> BatchCompleted => Observable.Empty<int>();
        public IObservable<IReadOnlyList<ClipEmbeddingRecord>> BatchRecordsCompleted =>
            Observable.Empty<IReadOnlyList<ClipEmbeddingRecord>>();

        public void Start() { StartCount++; IsRunning = true; }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public void Poke() { }
        public Task<EmbeddingCoverage> GetCoverageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<EmbeddingCoverage>(default!);
        public Task RerunAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
