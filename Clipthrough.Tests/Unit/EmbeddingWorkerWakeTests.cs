using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The wake signal used to be <c>try { if (_wake.CurrentCount == 0) _wake.Release(); } catch { }</c>,
/// which hid every failure behind the two that are actually expected. These
/// tests pin those two - a full semaphore and a disposed one - so the tolerance
/// they rely on cannot be removed silently, and so the catch cannot be widened
/// back to swallowing genuine defects.
/// </summary>
public sealed class EmbeddingWorkerWakeTests
{
    /// <summary>
    /// <c>CurrentCount == 0</c> followed by <c>Release()</c> is a check-then-act
    /// race: concurrent pokes can all observe an empty semaphore and all try to
    /// release it, and every loser gets <see cref="SemaphoreFullException"/>.
    /// Losing that race is the outcome the caller wanted anyway - the loop is
    /// already scheduled to run - so <c>Poke</c> must stay total.
    /// </summary>
    [Fact]
    public async Task Poke_ToleratesConcurrentCallersRacingOnTheWakeSemaphore()
    {
        using var worker = CreateWorker();

        const int Threads = 16;
        const int Rounds = 400;
        using var barrier = new Barrier(Threads);
        var failures = new ConcurrentQueue<Exception>();

        var pokers = new Task[Threads];
        for (var t = 0; t < Threads; t++)
        {
            pokers[t] = Task.Factory.StartNew(
                () =>
                {
                    for (var i = 0; i < Rounds; i++)
                    {
                        // Line every thread up so they all read CurrentCount in
                        // the same instant; that is the window the catch covers.
                        barrier.SignalAndWait();
                        try
                        {
                            worker.Poke();
                        }
                        catch (Exception ex)
                        {
                            failures.Enqueue(ex);
                        }

                        // Put the semaphore back to empty so the next round
                        // races again instead of short-circuiting on the guard.
                        DrainWake(worker);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        await Task.WhenAll(pokers);

        Assert.True(
            failures.IsEmpty,
            $"Poke threw {failures.Count} time(s); first was: {(failures.TryPeek(out var first) ? first.ToString() : "<none>")}");
    }

    /// <summary>
    /// Shutdown order is not guaranteed: a queued embedding request can poke a
    /// worker whose semaphore has already been disposed. That must not surface
    /// as an unhandled exception on a background thread.
    /// </summary>
    [Fact]
    public void SignalWake_ToleratesAWakeSemaphoreThatWasAlreadyDisposed()
    {
        var worker = CreateWorker();
        worker.Dispose();

        // Poke's own _disposed guard would short-circuit this, so drive the
        // signal directly - the point is that the catch, not the guard, is what
        // keeps a post-dispose wake harmless.
        InvokeSignalWake(worker);
    }

    private static EmbeddingWorker CreateWorker()
        => new(null!, null!, null!);

    private static void InvokeSignalWake(EmbeddingWorker worker)
    {
        var method = typeof(EmbeddingWorker).GetMethod(
            "SignalWake",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        try
        {
            method!.Invoke(worker, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static void DrainWake(EmbeddingWorker worker)
    {
        var field = typeof(EmbeddingWorker).GetField(
            "_wake",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var wake = (SemaphoreSlim)field!.GetValue(worker)!;
        while (wake.Wait(0))
        {
        }
    }
}
