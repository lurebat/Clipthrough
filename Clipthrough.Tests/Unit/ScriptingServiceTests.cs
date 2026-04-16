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
    public async Task EvaluateAsync_default_scripts_round_trip()
    {
        var svc = new ScriptingService();
        var scripts = ScriptingService.GetDefaultScripts();
        Assert.NotEmpty(scripts);

        var quote = await svc.EvaluateAsync(FindCode(scripts, "JSON quote"), "hello \"world\"");
        Assert.Equal("\"hello \\u0022world\\u0022\"", quote);

        var unquoted = await svc.EvaluateAsync(FindCode(scripts, "JSON unquote"), quote);
        Assert.Equal("hello \"world\"", unquoted);

        var urlEnc = await svc.EvaluateAsync(FindCode(scripts, "URL encode"), "a b&c");
        Assert.Equal("a%20b%26c", urlEnc);

        var b64 = await svc.EvaluateAsync(FindCode(scripts, "Base64 encode"), "hi");
        Assert.Equal("aGk=", b64);
    }

    private static string FindCode(System.Collections.Generic.IReadOnlyList<Models.UserScript> list, string name)
    {
        foreach (var s in list)
        {
            if (s.Name == name) return s.Code;
        }
        throw new System.InvalidOperationException($"missing default script {name}");
    }
}
