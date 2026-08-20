using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// <c>StopAsync</c> exists so a caller about to move, rekey or replace the
/// database file can be sure nothing clipboard-originated is still writing to
/// it. <c>Stop</c> cannot promise that: off the UI thread it only posts, and
/// neither form waits for the enrichment a capture spawns fire-and-forget -
/// which performs its own writes, including a retention pass, well after the
/// capture that started it has returned. (round 2, arch-sol A6)
/// </summary>
/// <remarks>
/// A real capture cannot be scheduled from a test - it is private, driven by a
/// Win32 message, and needs a real clipboard - so these reach the same
/// primitives the capture uses and assert the property directly, the approach
/// <c>ClipboardMonitorServiceDisposalTests</c> already takes.
///
/// AvaloniaFact rather than Fact: StopAsync touches the dispatcher, and a plain
/// Fact would hang rather than fail if that ever stopped being satisfiable.
/// </remarks>
public sealed class ClipboardMonitorServiceStopTests
{
    private static ClipboardMonitorService CreateService()
        => new(null!, null!, null!, null!);

    private static T Field<T>(ClipboardMonitorService service, string name)
    {
        var field = typeof(ClipboardMonitorService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} is gone - update this test to match the new field name.");
        return (T)field.GetValue(service)!;
    }

    private static void TrackEnrichment(ClipboardMonitorService service, Task enrichment)
    {
        var method = typeof(ClipboardMonitorService).GetMethod("TrackEnrichment", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TrackEnrichment is gone - update this test to match.");
        method.Invoke(service, [enrichment]);
    }

    [AvaloniaFact]
    public async Task StopAsync_DoesNotReturnWhileACaptureIsStillHoldingTheGate()
    {
        using var service = CreateService();
        var gate = Field<SemaphoreSlim>(service, "_captureGate");

        // Model a capture that is inside the body - past the point where it
        // decided to write - when maintenance begins.
        Assert.True(gate.Wait(0), "the gate was already held; the test would prove nothing");

        var stopping = service.StopAsync();
        Assert.False(
            stopping.IsCompleted,
            "StopAsync returned while a capture still held the gate, so maintenance could clear the pools under an in-flight write");

        gate.Release();
        await stopping;
    }

    [AvaloniaFact]
    public async Task StopAsync_DoesNotReturnWhileEnrichmentIsStillWriting()
    {
        using var service = CreateService();
        var enrichment = new TaskCompletionSource();

        // Enrichment outlives the capture that spawned it: the deferred content
        // update, the sensitivity scan, the icon write and the retention pass
        // all run after the gate has been released.
        TrackEnrichment(service, enrichment.Task);

        var stopping = service.StopAsync();
        Assert.False(
            stopping.IsCompleted,
            "StopAsync returned while enrichment was still writing to the database");

        enrichment.SetResult();
        await stopping;
    }

    /// <summary>
    /// The control. Without it "never return" would satisfy both tests above.
    /// </summary>
    [AvaloniaFact]
    public async Task StopAsync_ReturnsPromptlyWithNothingInFlight()
    {
        using var service = CreateService();

        await service.StopAsync();

        Assert.False(service.IsRunning);
    }

    /// <summary>
    /// Enrichment tasks are swept as they complete, so a long session does not
    /// accumulate a task handle per clip ever captured.
    /// </summary>
    [AvaloniaFact]
    public async Task TrackedEnrichment_DoesNotAccumulateOnceComplete()
    {
        using var service = CreateService();

        for (var i = 0; i < 50; i++)
        {
            TrackEnrichment(service, Task.CompletedTask);
        }

        var tracked = Field<System.Collections.Generic.List<Task>>(service, "_enrichmentTasks");

        // Not Assert.Single: on a List<Task> it returns the Task, which the
        // compiler then flags as an un-awaited call (CS4014).
        Assert.Equal(1, tracked.Count);

        await service.StopAsync();
    }
}
