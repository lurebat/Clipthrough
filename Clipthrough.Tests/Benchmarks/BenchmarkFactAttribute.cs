using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace Clipthrough.Tests.Benchmarks;

/// <summary>
/// A fact that runs only when CLIPTHROUGH_BENCH=1. Benchmarks seed a 1 GB
/// library and take minutes; they are measurements, not pass/fail tests.
/// </summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public const string OptInVariable = "CLIPTHROUGH_BENCH";

    public BenchmarkFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Skip = $"Opt-in benchmark: set {OptInVariable}=1 to run.";
        }
    }
}