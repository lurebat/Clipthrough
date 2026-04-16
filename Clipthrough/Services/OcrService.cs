using System;
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

    public OcrService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

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

    private OcrEngine? BuildEngine(string languages)
    {
        var requested = string.IsNullOrWhiteSpace(languages) ? _settingsService.Current.OcrLanguages : languages;
        if (string.IsNullOrWhiteSpace(requested))
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }

        foreach (var tag in requested.Split(new[] { '+', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var lang = new Language(tag.Trim());
                if (OcrEngine.IsLanguageSupported(lang))
                {
                    var engine = OcrEngine.TryCreateFromLanguage(lang);
                    if (engine is not null)
                    {
                        return engine;
                    }
                }
            }
            catch
            {
                // unsupported tag — try the next one
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
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
