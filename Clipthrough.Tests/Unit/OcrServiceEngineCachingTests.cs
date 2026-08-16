using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Windows.Media.Ocr;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Proves <see cref="OcrService"/> is actually wired to the engine cache and
/// derives the right key. Without this the cache could be dead code and every
/// <c>OcrEngineCacheTests</c> assertion would still pass - the exact shape of a
/// test that reports safety it does not provide.
///
/// Real engine construction needs an installed Windows language pack, which
/// would make these machine-dependent, so the engines are uninitialized
/// instances used purely as reference identities. Nothing is ever called on
/// them; what is under test is the routing and the key, not WinRT.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class OcrServiceEngineCachingTests
{
    [Fact]
    public void BuildEngine_ReusesTheEngineAcrossCalls()
    {
        var service = CreateService(ocrLanguages: "en");
        var builds = 0;

        var first = service.BuildEngine("en", _ => Matched(ref builds));
        var second = service.BuildEngine("en", _ => Matched(ref builds));
        var third = service.BuildEngine("en", _ => Matched(ref builds));

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal(1, builds);

        // The count comes from the cache instance the service holds, so it also
        // witnesses that the service goes through the cache at all.
        Assert.Equal(1, service.EngineBuildCount);
    }

    [Fact]
    public void BuildEngine_FallsBackToTheConfiguredLanguagesWhenNoneAreRequested()
    {
        var service = CreateService(ocrLanguages: "he");
        string? seenKey = null;
        var builds = 0;

        service.BuildEngine(
            string.Empty,
            key =>
            {
                seenKey = key;
                return Matched(ref builds);
            });

        Assert.Equal("he", seenKey);
    }

    [Fact]
    public void BuildEngine_PrefersAnExplicitlyRequestedLanguageOverTheConfiguredOne()
    {
        var service = CreateService(ocrLanguages: "he");
        string? seenKey = null;
        var builds = 0;

        service.BuildEngine(
            "en",
            key =>
            {
                seenKey = key;
                return Matched(ref builds);
            });

        Assert.Equal("en", seenKey);
    }

    [Fact]
    public void BuildEngine_RebuildsAfterTheConfiguredLanguagesChange()
    {
        var settings = new StubSettingsService(AppSettings.Default with { OcrLanguages = "en" });
        var service = new OcrService(settings);
        var builds = 0;

        var english = service.BuildEngine(string.Empty, _ => Matched(ref builds));

        settings.Replace(AppSettings.Default with { OcrLanguages = "he" });
        var hebrew = service.BuildEngine(string.Empty, _ => Matched(ref builds));

        Assert.NotNull(english);
        Assert.NotSame(english, hebrew);
        Assert.Equal(2, builds);
    }

    [Fact]
    public void BuildEngine_IgnoresSurroundingWhitespaceInTheConfiguredLanguages()
    {
        var service = CreateService(ocrLanguages: "  en  ");
        var builds = 0;

        var padded = service.BuildEngine(string.Empty, _ => Matched(ref builds));
        var exact = service.BuildEngine("en", _ => Matched(ref builds));

        Assert.NotNull(padded);
        Assert.Same(padded, exact);
        Assert.Equal(1, builds);
    }

    private static OcrService CreateService(string ocrLanguages)
        => new(new StubSettingsService(AppSettings.Default with { OcrLanguages = ocrLanguages }));

    /// <summary>
    /// A distinct engine reference per build, so "same engine" assertions test
    /// reuse rather than accidental equality.
    /// </summary>
    private static OcrEngineCreation<OcrEngine> Matched(ref int builds)
    {
        builds++;
        var engine = (OcrEngine)RuntimeHelpers.GetUninitializedObject(typeof(OcrEngine));
        return new OcrEngineCreation<OcrEngine>(engine, MatchedRequestedLanguage: true);
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public StubSettingsService(AppSettings current) => Current = current;

        public AppSettings Current { get; private set; }

        public bool HasSavedSettings => true;

        public event EventHandler<AppSettings>? SettingsChanged { add { } remove { } }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            => UpdateAsync(_ => settings, cancellationToken);

        public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            Current = mutate(Current);
            return Task.FromResult(Current);
        }

        public void Replace(AppSettings settings) => Current = settings;
    }
}
