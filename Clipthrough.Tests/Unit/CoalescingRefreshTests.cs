using System;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>F-25: overlapping coverage refreshes collapse into one trailing run.</summary>
public sealed class CoalescingRefreshTests
{
    [Fact]
    public async Task TriggersDuringARefresh_CollapseIntoOneTrailingRun()
    {
        var runs = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = new CoalescingRefresh(async () =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                await gate.Task;
            }
        });

        var first = refresh.RunAsync();
        var overlapping = new[] { refresh.RunAsync(), refresh.RunAsync(), refresh.RunAsync() };
        await Task.WhenAll(overlapping);
        Assert.Equal(1, runs);

        gate.SetResult();
        await first;

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task SequentialTriggers_EachRun()
    {
        var runs = 0;
        var refresh = new CoalescingRefresh(() => { runs++; return Task.CompletedTask; });

        await refresh.RunAsync();
        await refresh.RunAsync();

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task AFailedRefresh_DoesNotWedgeLaterOnes()
    {
        var runs = 0;
        var refresh = new CoalescingRefresh(() => ++runs == 1 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(refresh.RunAsync);
        await refresh.RunAsync();

        Assert.Equal(2, runs);
    }
}