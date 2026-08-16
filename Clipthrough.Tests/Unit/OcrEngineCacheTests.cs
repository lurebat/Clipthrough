using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Building a Windows OCR engine loads a language model. <c>OcrService</c> used
/// to build one per image, so importing a backlog reloaded the same model once
/// per clip. These tests pin the caching policy - including the two cases it
/// deliberately refuses to cache, because caching them would strand the user on
/// a stale answer after they install a language pack.
/// </summary>
public sealed class OcrEngineCacheTests
{
    [Fact]
    public void GetOrCreate_BuildsTheEngineOnlyOnceForRepeatedRequests()
    {
        var cache = new OcrEngineCache<object>();
        var first = cache.GetOrCreate("en", Matched);
        var second = cache.GetOrCreate("en", Matched);
        var third = cache.GetOrCreate("en", Matched);

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal(1, cache.CreateCount);
    }

    [Fact]
    public void GetOrCreate_RebuildsWhenTheRequestedLanguagesChange()
    {
        var cache = new OcrEngineCache<object>();
        var english = cache.GetOrCreate("en", Matched);
        var hebrew = cache.GetOrCreate("he", Matched);

        Assert.NotSame(english, hebrew);
        Assert.Equal(2, cache.CreateCount);

        // ...and going back does not resurrect the first engine, because only
        // the most recent key is held.
        var englishAgain = cache.GetOrCreate("en", Matched);
        Assert.NotSame(english, englishAgain);
        Assert.Equal(3, cache.CreateCount);
    }

    [Fact]
    public void GetOrCreate_TreatsTheLanguageKeyCaseInsensitively()
    {
        var cache = new OcrEngineCache<object>();
        var lower = cache.GetOrCreate("en-us", Matched);
        var upper = cache.GetOrCreate("EN-US", Matched);

        Assert.Same(lower, upper);
        Assert.Equal(1, cache.CreateCount);
    }

    [Fact]
    public void GetOrCreate_CachesTheProfileEngineForAnEmptyRequest()
    {
        var cache = new OcrEngineCache<object>();
        var first = cache.GetOrCreate(string.Empty, Matched);
        var second = cache.GetOrCreate(string.Empty, Matched);

        Assert.Same(first, second);
        Assert.Equal(1, cache.CreateCount);
    }

    /// <summary>
    /// A miss means the language pack is missing, and the user can install one
    /// without restarting the app. Caching the miss would keep OCR broken.
    /// </summary>
    [Fact]
    public void GetOrCreate_DoesNotCacheAFailedBuild()
    {
        var cache = new OcrEngineCache<object>();

        Assert.Null(cache.GetOrCreate("en", _ => new OcrEngineCreation<object>(null, MatchedRequestedLanguage: false)));
        Assert.Null(cache.GetOrCreate("en", _ => new OcrEngineCreation<object>(null, MatchedRequestedLanguage: false)));
        Assert.Equal(2, cache.CreateCount);

        // The pack arrives, and the very next call must pick it up.
        var engine = cache.GetOrCreate("en", Matched);
        Assert.NotNull(engine);
        Assert.Equal(3, cache.CreateCount);
    }

    /// <summary>
    /// Falling back to the user-profile languages means the requested pack is
    /// not installed yet. Caching the fallback under the requested key would
    /// keep serving the wrong language after the user installs the right one.
    /// </summary>
    [Fact]
    public void GetOrCreate_DoesNotCacheAProfileFallbackAgainstARequestedLanguage()
    {
        var cache = new OcrEngineCache<object>();

        var fallbackA = cache.GetOrCreate("he", Fallback);
        var fallbackB = cache.GetOrCreate("he", Fallback);

        Assert.NotNull(fallbackA);
        Assert.NotSame(fallbackA, fallbackB);
        Assert.Equal(2, cache.CreateCount);

        // The Hebrew pack is installed, so the next call must return the real
        // engine rather than a remembered fallback.
        var matched = cache.GetOrCreate("he", Matched);
        Assert.NotSame(fallbackB, matched);
        Assert.Equal(3, cache.CreateCount);
    }

    [Fact]
    public async Task GetOrCreate_ReturnsTheSameEngineToConcurrentCallers()
    {
        var cache = new OcrEngineCache<object>();

        // Warm it up first: a cold race may legitimately build twice, but once
        // an engine is cached every caller has to get that one.
        var expected = cache.GetOrCreate("en", Matched);

        var readers = new Task<object?>[32];
        for (var i = 0; i < readers.Length; i++)
        {
            readers[i] = Task.Run(() => cache.GetOrCreate("en", Matched));
        }

        var results = await Task.WhenAll(readers);

        Assert.All(results, r => Assert.Same(expected, r));
        Assert.Equal(1, cache.CreateCount);
    }

    private static OcrEngineCreation<object> Matched(string key)
        => new(new Engine(key, IsFallback: false), MatchedRequestedLanguage: true);

    private static OcrEngineCreation<object> Fallback(string key)
        => new(new Engine(key, IsFallback: true), MatchedRequestedLanguage: false);

    private sealed record Engine(string Key, bool IsFallback)
    {
        // Reference identity is what the tests assert on, so two engines built
        // for the same key must not compare equal.
        public bool Equals(Engine? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
}
