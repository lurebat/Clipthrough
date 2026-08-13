using System;
using System.Linq;
using System.Reflection;
using Clipthrough.Presentation;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Every background-progress stream has to go through
/// <see cref="ProgressStreams.RateLimit"/>.
///
/// ProgressStreamsTests pins what RateLimit does; this pins that the progress
/// streams actually use it. They were originally written with Rx's
/// <c>Throttle</c>, which reads like a rate limiter and is a debounce, so both
/// coverage figures stopped updating for as long as their work kept producing
/// results. Nothing about the call site makes the mistake visible, and the
/// remaining Throttle calls in the same file are all correct - they debounce
/// user input, which is what a debounce is for.
/// </summary>
public sealed class ProgressStreamWiringTests
{
    // Semantic-embedding coverage and OCR-queue coverage.
    private const int ProgressStreamCount = 2;

    [Fact]
    public void EveryProgressStream_IsRateLimitedRatherThanDebounced()
    {
        var rateLimit = typeof(ProgressStreams)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "RateLimit");

        var found = IlCallScanner.CountCalls(typeof(MainWindowViewModel), rateLimit);

        Assert.True(
            found == ProgressStreamCount,
            $"Expected {ProgressStreamCount} progress streams wired through ProgressStreams.RateLimit, found {found}. " +
            "If you added a progress stream, rate-limit it and raise the count. If one went back to Throttle, " +
            "it now reports nothing while the work it describes is still running.");
    }
}
