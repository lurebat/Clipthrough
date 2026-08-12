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
    // Bug #5: RestoreBackupAsync stops the clipboard monitor, OCR queue, and
    // embedding worker before the swap. On success the app exits, but on FAILURE
    // the workers must be restarted — otherwise the running session silently stops
    // capturing clips until the next launch.
    [AvaloniaFact]
    public async Task RestoreBackup_WhenRestoreFails_RestartsStoppedWorkers()
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

        Assert.Equal(1, monitor.StopCount);
        Assert.Equal(1, monitor.StartCount);
        Assert.Equal(1, ocr.StartCount);
        Assert.Equal(1, embedding.StartCount);
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
        public void SuppressNext() { }
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
