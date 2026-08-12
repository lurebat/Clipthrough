using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

public sealed class BackgroundOcrQueueTests
{
    // K5: a clip is marked 'running' while the queue holds it, but
    // TryClaimForOcrAsync refuses to reclaim a 'running' row. Stopping the queue
    // mid-job (app exit, database maintenance) therefore left the marker behind
    // and the clip could never be OCR'd again — the backlog re-queued it on every
    // launch and the claim silently declined it every time.
    [Fact]
    public async Task ResetStalledOcrClaims_MakesAnInterruptedClipClaimableAgain()
    {
        using var scope = CreateScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var clip = await CaptureImageClipAsync(scope);

        // The claim leaves the row 'running'; nothing can pick it up after that.
        Assert.True(await scope.ClipStoreService.TryClaimForOcrAsync(clip.Id));
        Assert.False(await scope.ClipStoreService.TryClaimForOcrAsync(clip.Id));
        Assert.DoesNotContain(clip.Id, await scope.ClipStoreService.GetPendingOcrClipIdsAsync());

        var reset = await scope.ClipStoreService.ResetStalledOcrClaimsAsync();

        Assert.Equal(1, reset);
        Assert.Contains(clip.Id, await scope.ClipStoreService.GetPendingOcrClipIdsAsync());
        Assert.True(await scope.ClipStoreService.TryClaimForOcrAsync(clip.Id));
    }

    // The reset must not disturb work that already reached a terminal state:
    // re-queueing succeeded clips would re-run OCR over the whole library.
    [Fact]
    public async Task ResetStalledOcrClaims_LeavesCompletedClipsAlone()
    {
        using var scope = CreateScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var clip = await CaptureImageClipAsync(scope);

        Assert.True(await scope.ClipStoreService.TryClaimForOcrAsync(clip.Id));
        Assert.True(await scope.ClipStoreService.SetOcrResultAsync(clip.Id, "done"));

        var reset = await scope.ClipStoreService.ResetStalledOcrClaimsAsync();

        Assert.Equal(0, reset);
        Assert.DoesNotContain(clip.Id, await scope.ClipStoreService.GetPendingOcrClipIdsAsync());
    }

    // EnqueueBacklogAsync runs when nothing is in flight, so it is the place that
    // recovers rows a previous stop stranded in 'running'.
    [Fact]
    public async Task EnqueueBacklog_RecoversAClipStrandedInRunning()
    {
        using var scope = CreateScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var clip = await CaptureImageClipAsync(scope);
        Assert.True(await scope.ClipStoreService.TryClaimForOcrAsync(clip.Id));

        var ocr = new GatedOcrService(gateFirstJob: false);
        using var queue = new BackgroundOcrQueue(scope.ClipStoreService, ocr, scope.SettingsService);
        var completed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = queue.OcrCompleted.Subscribe(id => completed.TrySetResult(id));

        queue.Start();
        await queue.EnqueueBacklogAsync();

        var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await queue.StopAsync();

        Assert.Same(completed.Task, finished);
        Assert.Equal(clip.Id, await completed.Task);
    }

    // K6: ids still sitting in the channel when the queue stops never reach the
    // worker's finally block, so they stay in the in-flight set. Enqueue treats a
    // present id as already queued, so after a restart the backlog silently
    // dropped exactly the clips the stop had interrupted.
    [Fact]
    public async Task StopAsync_ThenRestart_ReprocessesTheClipTheStopLeftQueued()
    {
        using var scope = CreateScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var first = await CaptureImageClipAsync(scope);
        var second = await CaptureImageClipAsync(scope);

        var ocr = new GatedOcrService(gateFirstJob: true);
        using var queue = new BackgroundOcrQueue(scope.ClipStoreService, ocr, scope.SettingsService);

        queue.Start();
        queue.Enqueue(first.Id);
        queue.Enqueue(second.Id);

        // The first clip is inside the OCR service; the second is still queued.
        await ocr.EnteredFirstJob.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = queue.StopAsync();
        ocr.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        // The stop discarded the second clip's queue slot without processing it.
        Assert.Contains(second.Id, await scope.ClipStoreService.GetPendingOcrClipIdsAsync());

        var completed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = queue.OcrCompleted.Subscribe(id =>
        {
            if (id == second.Id)
            {
                completed.TrySetResult(id);
            }
        });

        queue.Start();
        queue.Enqueue(second.Id);

        var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await queue.StopAsync();

        Assert.Same(completed.Task, finished);
    }

    private static TemporaryDatabaseScope CreateScope()
    {
        var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1 << 20 });
        return scope;
    }

    private static async Task<ClipEntry> CaptureImageClipAsync(TemporaryDatabaseScope scope)
    {
        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentBytes = Encoding.UTF8.GetBytes($"image bytes {Guid.NewGuid():N}"),
            SourceApp = "Test",
        });

        Assert.NotNull(clip);
        return clip;
    }

    /// <summary>
    /// Optionally blocks inside the first extraction so a test can stop the queue
    /// while one job is in the service and another is still queued behind it.
    /// </summary>
    private sealed class GatedOcrService(bool gateFirstJob) : IOcrService
    {
        private readonly SemaphoreSlim _gate = new(0, 1);
        private int _calls;

        public TaskCompletionSource<bool> EnteredFirstJob { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;

        public void Release() => _gate.Release();

        public async Task<OcrResult> ExtractTextAsync(byte[] imageBytes, string languages, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                EnteredFirstJob.TrySetResult(true);
                if (gateFirstJob)
                {
                    await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            return new OcrResult(true, "text", null);
        }
    }
}
