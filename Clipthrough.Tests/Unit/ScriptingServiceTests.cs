using System;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

public class ScriptingServiceTests
{
    [Fact]
    public async Task EvaluateAsync_empty_code_returns_input()
    {
        var svc = new ScriptingService();
        var result = await svc.EvaluateAsync("  ", "hello");
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task EvaluateAsync_runs_simple_expression()
    {
        var svc = new ScriptingService();
        var result = await svc.EvaluateAsync("Input.ToUpperInvariant()", "abc");
        Assert.Equal("ABC", result);
    }

    // Behavioral: same source code must compile exactly once across repeated calls.
    [Fact]
    public async Task EvaluateAsync_caches_compiled_script_compile_count_stays_at_one()
    {
        var svc = new ScriptingService();

        var first  = await svc.EvaluateAsync("Input + Input", "ab");
        var second = await svc.EvaluateAsync("Input + Input", "cd");

        Assert.Equal("abab", first);
        Assert.Equal("cdcd", second);
        // Identical source: second call must be a cache hit — compile count must be 1.
        Assert.Equal(1, svc.CompileCount);
    }

    // Behavioral: distinct scripts each compile once; cache size is bounded.
    [Fact]
    public async Task EvaluateAsync_evicts_oldest_entry_when_capacity_exceeded()
    {
        // Use a small cap so the test stays fast.
        const int cap = 3;
        var svc = new ScriptingService(maxCachedScripts: cap);

        // Fill the cache exactly to capacity.
        for (var i = 0; i < cap; i++)
        {
            await svc.EvaluateAsync($"\"{i}\"", string.Empty);
        }

        Assert.Equal(cap, svc.CacheCount);
        Assert.Equal(cap, svc.CompileCount);

        // Adding one more script should evict one and keep count ≤ cap.
        await svc.EvaluateAsync($"\"{cap}\"", string.Empty);

        Assert.True(svc.CacheCount <= cap, $"Cache should not grow past cap={cap}, was {svc.CacheCount}");
        // cap+1 unique scripts → cap+1 compilations total.
        Assert.Equal(cap + 1, svc.CompileCount);
    }

    [Fact]
    public async Task EvaluateAsync_surfaces_compilation_errors_as_invalid_operation()
    {
        var svc = new ScriptingService();
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => svc.EvaluateAsync("this is not valid c#", "x"));
    }

    // Hard wall-clock watchdog: a non-terminating script must be killed at timeout.
    // Uses a very short timeout so the test completes quickly.
    [Fact]
    public async Task EvaluateAsync_kills_non_terminating_script_at_timeout()
    {
        var svc = new ScriptingService(scriptTimeout: TimeSpan.FromMilliseconds(400));

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => svc.EvaluateAsync("while(true) {}", string.Empty));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Caller-cancelled CancellationToken must still propagate as OCE, not TimeoutException.
    [Fact]
    public async Task EvaluateAsync_caller_cancellation_propagates_as_operation_cancelled()
    {
        var svc = new ScriptingService(scriptTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.EvaluateAsync("while(true) {}", string.Empty, cts.Token));
    }
}
