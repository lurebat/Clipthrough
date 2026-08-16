using System;

namespace Clipthrough.Services;

/// <summary>
/// The result of one attempt to build an OCR engine.
/// </summary>
/// <param name="Engine">The engine, or <c>null</c> when none could be built.</param>
/// <param name="MatchedRequestedLanguage">
/// True when the engine was built from one of the tags the caller asked for,
/// false when the caller's tags were all unavailable and the user-profile
/// languages were used instead.
/// </param>
internal readonly record struct OcrEngineCreation<T>(T? Engine, bool MatchedRequestedLanguage)
    where T : class;

/// <summary>
/// Remembers the last OCR engine so a backlog of images does not reload the
/// recognizer model once per clip. Building a Windows OCR engine loads a
/// language model, which dwarfs the per-image recognition cost when a large
/// import is being processed.
/// </summary>
/// <remarks>
/// Generic over the engine type purely so this policy carries no dependency on
/// the Windows-only OCR types and can be exercised directly by tests.
///
/// Two rules are deliberate:
///
/// <list type="bullet">
/// <item>A failed build is never cached. <c>TryCreateFromLanguage</c> returning
/// null almost always means the language pack is missing, and the user can
/// install one while the app is running; a cached miss would keep OCR broken
/// until restart.</item>
/// <item>A user-profile fallback is not cached against a non-empty key, for the
/// same reason: the user asked for a language that is not installed yet, and
/// caching the fallback would keep serving it after they install the pack.</item>
/// </list>
///
/// Two callers racing on a cold cache can both build an engine. That is benign -
/// one wins the store and the other's engine is collected - and cheaper than
/// holding the lock across a model load.
/// </remarks>
internal sealed class OcrEngineCache<T>
    where T : class
{
    private readonly object _gate = new();
    private string? _key;
    private T? _engine;

    /// <summary>Number of times <paramref name="create"/> was actually invoked.</summary>
    public int CreateCount { get; private set; }

    public T? GetOrCreate(string key, Func<string, OcrEngineCreation<T>> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        lock (_gate)
        {
            if (_engine is not null && string.Equals(_key, key, StringComparison.OrdinalIgnoreCase))
            {
                return _engine;
            }
        }

        var creation = create(key);

        lock (_gate)
        {
            CreateCount++;
        }

        if (creation.Engine is null)
        {
            return null;
        }

        if (key.Length > 0 && !creation.MatchedRequestedLanguage)
        {
            return creation.Engine;
        }

        lock (_gate)
        {
            _key = key;
            _engine = creation.Engine;
        }

        return creation.Engine;
    }
}
