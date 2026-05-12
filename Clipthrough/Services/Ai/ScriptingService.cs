using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Clipthrough.Services;

public sealed class ScriptingService : IScriptingService
{
    private static readonly ScriptOptions DefaultOptions = ScriptOptions.Default
        .AddReferences(
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(Regex).Assembly,
            typeof(JsonDocument).Assembly,
            typeof(Encoding).Assembly)
        .AddImports(
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "System.Text",
            "System.Text.Json",
            "System.Text.RegularExpressions");

    // Compiling a Roslyn script is expensive (~hundreds of ms first time, tens
    // of ms subsequently). Cache compiled scripts so each unique source pays
    // the cost only once per process. Bounded to keep memory in check —
    // realistic libraries have a few dozen scripts at most.
    private const int MaxCachedScripts = 64;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Script<object?>> _scriptCache = new();

    public sealed class ScriptGlobals
    {
        public string Input { get; init; } = string.Empty;
    }

    public async Task<string> EvaluateAsync(string code, string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return input;
        }

        var script = GetOrCreateScript(code);
        var globals = new ScriptGlobals { Input = input ?? string.Empty };
        try
        {
            var state = await script.RunAsync(globals, cancellationToken).ConfigureAwait(false);
            return state.ReturnValue switch
            {
                null => string.Empty,
                string s => s,
                var other => other.ToString() ?? string.Empty,
            };
        }
        catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException ex)
        {
            // Surface the first diagnostic — it's the most actionable for the user.
            throw new InvalidOperationException(ex.Diagnostics.FirstOrDefault()?.GetMessage() ?? ex.Message, ex);
        }
    }

    private Script<object?> GetOrCreateScript(string code)
    {
        if (_scriptCache.TryGetValue(code, out var existing))
        {
            return existing;
        }

        var script = CSharpScript.Create<object?>(code, DefaultOptions, typeof(ScriptGlobals));
        // Forcing Compile here surfaces compilation errors immediately and
        // means subsequent RunAsync calls skip the compile step.
        script.Compile();

        if (_scriptCache.Count >= MaxCachedScripts)
        {
            // Cheap eviction: drop one entry. Avoids unbounded growth without
            // adding an LRU dependency. Realistic libraries stay below the cap.
            foreach (var key in _scriptCache.Keys)
            {
                _scriptCache.TryRemove(key, out _);
                break;
            }
        }

        _scriptCache[code] = script;
        return script;
    }
}
