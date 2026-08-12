using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Disposal races the capture that is already in flight. The capture is an
/// <c>async void</c> whose <c>finally</c> publishes to the busy subject and
/// releases the capture gate, and it spawns a fire-and-forget enrichment that
/// publishes updates - all of which run after <see cref="IDisposable.Dispose"/>
/// has returned. Nothing catches a throw from that <c>finally</c>, so tearing
/// the primitives down under it turns shutdown into an unhandled exception on
/// the dispatcher.
///
/// Those writers cannot be scheduled deterministically from a test (the capture
/// is private, driven by a Win32 message, and needs a real clipboard), so these
/// tests reach the primitives directly and assert the property the writers
/// depend on: after Dispose they are still safe to use.
/// </summary>
public sealed class ClipboardMonitorServiceDisposalTests
{
    [Fact]
    public void Dispose_LeavesTheCaptureSubjectsWritableForInFlightContinuations()
    {
        var service = CreateService();
        service.Dispose();

        // Each of these is an OnNext that a post-Dispose continuation performs:
        // the capture publishes the new clip, the enrichment publishes updates,
        // and the capture's finally clears the busy flag.
        Field<Subject<ClipEntry>>(service, "_capturedClips").OnNext(SampleClip());
        Field<Subject<ClipEntry>>(service, "_updatedClips").OnNext(SampleClip());
        Field<BehaviorSubject<bool>>(service, "_captureBusy").OnNext(false);
    }

    [Fact]
    public void Dispose_LeavesTheCaptureGateReleasableForInFlightContinuations()
    {
        var service = CreateService();
        var gate = Field<SemaphoreSlim>(service, "_captureGate");

        // Model a capture that is holding the gate when shutdown begins.
        Assert.True(gate.Wait(0));
        service.Dispose();

        gate.Release();
    }

    /// <summary>
    /// Not disposing the subjects must not become "not tearing them down at
    /// all": completing them is what drops the subscribers, and a subscriber
    /// that keeps receiving clips after shutdown is a leak in the other
    /// direction.
    /// </summary>
    [Fact]
    public void Dispose_CompletesTheObservablesAndStopsDeliveringToSubscribers()
    {
        var service = CreateService();
        var received = new List<ClipEntry>();
        var completed = false;
        using var subscription = service.CapturedClips.Subscribe(received.Add, () => completed = true);

        service.Dispose();
        Field<Subject<ClipEntry>>(service, "_capturedClips").OnNext(SampleClip());

        Assert.True(completed, "CapturedClips must complete on disposal.");
        Assert.Empty(received);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var service = CreateService();

        service.Dispose();
        service.Dispose();
    }

    /// <summary>
    /// The enrichment that follows a capture is fire-and-forget and keeps
    /// writing to the store across several awaits, so a clip captured just
    /// before shutdown carries on writing into a database that is closing or
    /// being rekeyed. It has to abandon the rest of its work instead.
    ///
    /// Observed through Trace because that is where the symptom surfaces: the
    /// enrichment catches everything and logs a failure, which is what lands in
    /// the user's session log on every shutdown.
    /// </summary>
    [Fact]
    public async Task Enrichment_AbandonsItsRemainingWorkOnceDisposed()
    {
        var service = CreateService();

        // Counterweight: while the service is live the enrichment does reach
        // the store, so a silent run after disposal means the guard fired -
        // not that this reflection call does nothing at all.
        Assert.NotEmpty(await RunEnrichmentAsync(service));

        service.Dispose();

        Assert.Empty(await RunEnrichmentAsync(service));
    }

    private static async Task<IReadOnlyList<string>> RunEnrichmentAsync(ClipboardMonitorService service)
    {
        var method = typeof(ClipboardMonitorService).GetMethod("EnrichCapturedClipAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnrichCapturedClipAsync is gone - update this test to match.");

        var sink = new ConcurrentQueue<string>();
        var listener = new TraceCaptureListener(sink);
        Trace.Listeners.Add(listener);
        try
        {
            await (Task)method.Invoke(service, [SampleClip(), null])!;
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        return [.. sink];
    }

    private sealed class TraceCaptureListener(ConcurrentQueue<string> sink) : TraceListener
    {
        public override void Write(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }
    }

    // The disposal path touches none of the dependencies - it only stops the
    // window hook (never attached here) and completes its own subjects.
    private static ClipboardMonitorService CreateService()
        => new(null!, null!, null!);

    private static ClipEntry SampleClip() => new()
    {
        Id = 1,
        Content = "clip",
        ContentType = ContentType.Text,
    };

    private static T Field<T>(ClipboardMonitorService service, string name)
    {
        var field = typeof(ClipboardMonitorService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} is gone - update this test to match the new field name.");
        return (T)field.GetValue(service)!;
    }
}
