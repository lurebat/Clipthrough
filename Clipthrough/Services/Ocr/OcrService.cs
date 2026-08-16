using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Clipthrough.Services;

/// <summary>
/// Uses the built-in Windows.Media.Ocr engine. Requires the corresponding language packs to be installed
/// on the machine (Settings → Time &amp; Language → Language → Add a language, with the optional OCR feature).
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class OcrService : IOcrService
{
    private readonly ISettingsService _settingsService;

    // Building an engine loads a language model, which dwarfs the per-image
    // recognition cost. ExtractTextAsync is driven by BackgroundOcrQueue's
    // single sequential consumer loop, so before this a backlog of images
    // reloaded the same model once per clip.
    private readonly OcrEngineCache<OcrEngine> _engineCache = new();

    public OcrService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    private static readonly char[] LanguageTagSeparators = ['+', ',', ';', ' '];

    public bool IsAvailable => OcrEngine.AvailableRecognizerLanguages?.Count > 0;

    public async Task<OcrResult> ExtractTextAsync(byte[] imageBytes, string languages, CancellationToken cancellationToken = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            return new OcrResult(false, string.Empty, "No image data");
        }

        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var ras = new InMemoryRandomAccessStream();
            await ras.WriteAsync(ms.ToArray().AsBuffer());
            ras.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ras);
            using var bitmap = await decoder.GetSoftwareBitmapAsync();

            var engine = BuildEngine(languages);
            if (engine is null)
            {
                var available = string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag));
                return new OcrResult(false, string.Empty, $"No OCR engine for '{languages}'. Installed: {available}. Add the language pack in Windows Settings.");
            }

            var result = await engine.RecognizeAsync(bitmap);
            var text = result?.Text ?? string.Empty;
            return new OcrResult(true, text.TrimEnd(), null);
        }
        catch (Exception ex)
        {
            return new OcrResult(false, string.Empty, ex.Message);
        }
    }

    private OcrEngine? BuildEngine(string languages) => BuildEngine(languages, CreateEngine);

    /// <summary>
    /// Resolves the effective language key and hands it to the cache. Split out
    /// from the Windows-only engine construction so a test can prove this path
    /// really routes through the cache instead of rebuilding per call.
    /// </summary>
    internal OcrEngine? BuildEngine(string languages, Func<string, OcrEngineCreation<OcrEngine>> create)
    {
        var requested = string.IsNullOrWhiteSpace(languages) ? _settingsService.Current.OcrLanguages : languages;
        var key = requested?.Trim() ?? string.Empty;
        return _engineCache.GetOrCreate(key, create);
    }

    /// <summary>Engines built so far. Test-only window onto the cache.</summary>
    internal int EngineBuildCount => _engineCache.CreateCount;

    private static OcrEngineCreation<OcrEngine> CreateEngine(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            // Nothing was asked for, so the profile engine is the match rather
            // than a fallback, and is safe to cache against the empty key.
            return new OcrEngineCreation<OcrEngine>(OcrEngine.TryCreateFromUserProfileLanguages(), MatchedRequestedLanguage: true);
        }

        foreach (var tag in requested.Split(LanguageTagSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var lang = new Language(tag.Trim());
                if (OcrEngine.IsLanguageSupported(lang))
                {
                    var engine = OcrEngine.TryCreateFromLanguage(lang);
                    if (engine is not null)
                    {
                        return new OcrEngineCreation<OcrEngine>(engine, MatchedRequestedLanguage: true);
                    }
                }
            }
            catch (Exception ex)
            {
                // A malformed tag throws ArgumentException, but engine creation
                // is WinRT and can fail in other ways too. Either way the point
                // is to try the next tag - just not silently.
                Trace.TraceWarning($"OCR: could not build an engine for language tag '{tag}': {ex}");
            }
        }

        return new OcrEngineCreation<OcrEngine>(OcrEngine.TryCreateFromUserProfileLanguages(), MatchedRequestedLanguage: false);
    }
}

internal static class OcrBufferExtensions
{
    public static IBuffer AsBuffer(this byte[] bytes)
    {
        var writer = new global::Windows.Storage.Streams.DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }
}
