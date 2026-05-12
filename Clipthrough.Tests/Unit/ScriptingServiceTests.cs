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

    [Fact]
    public async Task EvaluateAsync_caches_compiled_script_across_calls()
    {
        var svc = new ScriptingService();

        // First call compiles, second should reuse the cache. We don't assert
        // timing (flaky) but we do assert the script behaves identically
        // across repeated invocations.
        var first = await svc.EvaluateAsync("Input + Input", "ab");
        var second = await svc.EvaluateAsync("Input + Input", "cd");
        Assert.Equal("abab", first);
        Assert.Equal("cdcd", second);
    }

    [Fact]
    public async Task EvaluateAsync_surfaces_compilation_errors_as_invalid_operation()
    {
        var svc = new ScriptingService();
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => svc.EvaluateAsync("this is not valid c#", "x"));
    }
}
