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

/// <summary>
/// Local-only Roslyn script executor. This service is intentionally NOT network-reachable
/// after U10 removed the remote /transform endpoint. All security mitigations here apply
/// only to the local scripting UI.
///
/// Reference set: <see cref="DefaultOptions"/> references only the explicitly listed
/// assemblies (System.Private.CoreLib, LINQ, Regex, JSON, Text). System.IO, System.Diagnostics.Process,
/// and System.Reflection are not added as explicit references. Scripts that attempt to
/// access those APIs at compile time will fail. At runtime, type-forwarded symbols in
/// loaded assemblies may still be reachable — the hard <see cref="DefaultScriptTimeout"/> watchdog
/// is the primary resource-bounding mitigation.
/// </summary>
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
    public const int MaxCachedScripts = 64;

    /// <summary>
    /// Hard wall-clock timeout for a single script execution. Cooperative cancellation
    /// at await points alone cannot stop a tight <c>while(true)</c> loop. After this
    /// deadline the caller receives a <see cref="TimeoutException"/>; the background
    /// thread that the Roslyn script is running on may continue until its next
    /// cooperative cancellation check or until the process exits.
    /// </summary>
    internal static readonly TimeSpan DefaultScriptTimeout = TimeSpan.FromSeconds(10);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Script<object?>> _scriptCache = new();
    private readonly int _maxCachedScripts;
    private readonly TimeSpan _scriptTimeout;
    private int _compileCount;

    /// <summary>Number of times a script was compiled (as opposed to served from cache). For tests.</summary>
    internal int CompileCount => _compileCount;

    /// <summary>Current number of entries in the compiled-script cache. For tests.</summary>
    internal int CacheCount => _scriptCache.Count;

    /// <param name="scriptTimeout">Override for tests; production uses <see cref="DefaultScriptTimeout"/>.</param>
    /// <param name="maxCachedScripts">Override for tests; production uses <see cref="MaxCachedScripts"/>.</param>
    public ScriptingService(TimeSpan? scriptTimeout = null, int? maxCachedScripts = null)
    {
        _scriptTimeout = scriptTimeout ?? DefaultScriptTimeout;
        _maxCachedScripts = maxCachedScripts ?? MaxCachedScripts;
    }

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

        // Hard wall-clock watchdog: cooperative cancellation alone cannot stop a tight loop.
        // After _scriptTimeout the caller gets a TimeoutException; the background thread
        // running the script may continue until its next cooperative check or process exit.
        using var timeoutCts = new CancellationTokenSource(_scriptTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Run on a thread-pool thread so the script cannot block the calling thread while waiting.
        var scriptTask = Task.Run(() => script.RunAsync(globals, linkedCts.Token));

        try
        {
            // WaitAsync cancels the await (not the scriptTask) when the linked token fires.
            var state = await scriptTask.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            return state.ReturnValue switch
            {
                null => string.Empty,
                string s => s,
                var other => other.ToString() ?? string.Empty,
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning($"[ScriptingService] Script abandoned after {_scriptTimeout.TotalSeconds:0}s timeout.");
            throw new TimeoutException($"Script execution timed out after {_scriptTimeout.TotalSeconds:0}s.");
        }
        catch (CompilationErrorException ex)
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
        System.Threading.Interlocked.Increment(ref _compileCount);

        if (_scriptCache.Count >= _maxCachedScripts)
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
