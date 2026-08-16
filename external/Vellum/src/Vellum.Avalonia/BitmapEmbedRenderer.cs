using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;

namespace Vellum.Avalonia;

/// <summary>
/// Where the bytes of a pasted or dropped image go, and what to call them afterwards.
/// </summary>
/// <remarks>
/// <para>
/// An image arriving from outside — off the clipboard, out of a drop — is a block of bytes with no
/// name. The document model holds a <em>reference</em> in <see cref="ImageEmbed.Source"/> and never
/// pixels, so something has to turn one into the other, and what that something should do is the
/// host's decision rather than Vellum's: an application with a content-addressed store wants a key
/// out of it, one writing to disk wants a path, and one that never persists anything wants the
/// bytes carried inside the document.
/// </para>
/// <para>
/// <see cref="DataUriImageStore"/> is the last of those and the default, because it is the only
/// one that needs no cooperation from the host at all.
/// </para>
/// </remarks>
public interface IImageStore
{
    /// <summary>Stores image bytes and returns what <see cref="ImageEmbed.Source"/> should say.</summary>
    /// <param name="bytes">The image, in whatever format it arrived in.</param>
    /// <param name="mediaType">Its media type, such as <c>image/png</c>.</param>
    /// <returns>
    /// The source to record, or <see langword="null"/> to refuse the image — which is how a host
    /// rejects one that is too large, of the wrong type, or arriving faster than it can store.
    /// </returns>
    string? Save(ReadOnlySpan<byte> bytes, string mediaType);
}

/// <summary>
/// An image store that keeps the bytes inside the document, as a <c>data:</c> URL.
/// </summary>
/// <remarks>
/// <para>
/// Self-contained, which is exactly what makes it the right default and the wrong choice at scale:
/// the document can be moved, saved and re-opened anywhere with nothing alongside it, and every
/// copy of it carries every image. Base64 also costs a third more than the bytes it encodes, and
/// that inflated form is what HTML and JSON export will contain.
/// </para>
/// <para>
/// So there is a size limit, and it defaults to something a clipboard screenshot fits inside and a
/// photograph does not. An image over it is refused rather than truncated, and refusing produces a
/// document without that image instead of one with a corrupt one.
/// </para>
/// </remarks>
/// <param name="maximumBytes">The largest image to accept.</param>
public sealed class DataUriImageStore(int maximumBytes = DataUriImageStore.DefaultMaximumBytes)
    : IImageStore
{
    /// <summary>The default size limit, in bytes.</summary>
    public const int DefaultMaximumBytes = 4 * 1024 * 1024;

    /// <summary>The largest image this store will accept.</summary>
    public int MaximumBytes { get; } = maximumBytes > 0
        ? maximumBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumBytes));

    /// <inheritdoc/>
    public string? Save(ReadOnlySpan<byte> bytes, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        return bytes.Length == 0 || bytes.Length > MaximumBytes
            ? null
            : $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }
}

/// <summary>
/// An <see cref="IEmbedRenderer"/> that actually draws images.
/// </summary>
/// <remarks>
/// <para>
/// Resolves <see cref="ImageEmbed.Source"/> and caches what it decodes, since the same source
/// appears once per layout pass and a document may hold the same image many times.
/// </para>
/// <para>
/// <b>By default it draws only pixels carried inside the document itself</b> — <c>data:</c> URLs,
/// and nothing else. That is the whole security stance, and it follows from where documents come
/// from: one arrives by paste or drop from an arbitrary application, so any source the renderer
/// resolves is an address that untrusted content chose. Fetching <c>http:</c> would make the
/// user's machine issue a request to it, which is both a tracking pixel and a request forgery.
/// Opening <c>file:</c> or a filesystem path would read a local file the user never pointed at
/// and put it on screen. Neither is a capability a paste should carry.
/// </para>
/// <para>
/// A host whose documents come from somewhere it trusts — its own store, its own assets — sets
/// <see cref="AllowLocalSources"/> to resolve <c>avares:</c>, <c>file:</c> and paths. Network
/// sources are never resolved by this type at all: an application that wants them should fetch
/// under its own policy and hand over the bytes.
/// </para>
/// <para>
/// Payloads are bounded by <see cref="MaximumSourceBytes"/> and decoded images by
/// <see cref="MaximumPixels"/>, because a small compressed image can describe an enormous one.
/// PNG dimensions are checked before decoding, from the header; other formats are checked after,
/// so a bomb in one of those still costs a transient allocation. A host taking wholly untrusted
/// documents should screen them itself rather than rely on that.
/// </para>
/// <para>
/// Nothing here throws. An image that cannot be decoded, or is refused, draws as the outlined box
/// the placeholder renderer would have drawn, because a document containing one bad image must
/// still render.
/// </para>
/// </remarks>
public sealed class BitmapEmbedRenderer : IEmbedRenderer, IDisposable
{
    private readonly ConcurrentDictionary<string, Bitmap?> _cache = new(StringComparer.Ordinal);
    private readonly IEmbedRenderer _fallback = new PlaceholderEmbedRenderer();
    private readonly bool _shared;
    private bool _disposed;

    /// <summary>Creates a renderer with its own cache.</summary>
    public BitmapEmbedRenderer()
    {
    }

    private BitmapEmbedRenderer(bool shared) => _shared = shared;

    /// <summary>The renderer every presenter uses unless it is given another.</summary>
    /// <remarks>
    /// <para>
    /// Shared because the cache is the expensive part and a control is not the right thing to
    /// scope it to. A history list showing two hundred documents is two hundred presenters, and
    /// giving each its own cache means holding the same image two hundred times over while
    /// decoding it two hundred times.
    /// </para>
    /// <para>
    /// Its <see cref="Dispose"/> does nothing. A host that replaces
    /// <see cref="DocumentPresenter.Embeds"/> and disposes what was there would otherwise
    /// permanently disable image rendering for every other control in the process.
    /// </para>
    /// </remarks>
    public static BitmapEmbedRenderer Shared { get; } = new(shared: true);

    /// <summary>How many distinct sources to keep decoded.</summary>
    /// <remarks>
    /// A bound rather than none: a clipboard history browsing hundreds of documents would
    /// otherwise hold every image any of them ever showed. At the limit an existing entry is
    /// evicted and disposed to make room, so every decoded bitmap is owned by the cache and freed
    /// with it.
    /// </remarks>
    public int CacheLimit { get; init; } = 64;

    /// <summary>The largest encoded payload to decode, in bytes.</summary>
    public int MaximumSourceBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>The most pixels an image may decode to.</summary>
    /// <remarks>
    /// Pixels rather than bytes because that is what the allocation scales with: 64 megapixels is
    /// roughly a quarter of a gigabyte once decoded, and larger than any image a document has a
    /// reason to contain.
    /// </remarks>
    public long MaximumPixels { get; init; } = 64_000_000;

    /// <summary>
    /// Whether to resolve sources outside the document — <c>avares:</c>, <c>file:</c> and
    /// filesystem paths.
    /// </summary>
    /// <remarks>
    /// Off by default. Turning it on is safe exactly when the documents being rendered are
    /// trusted, and unsafe when they are pasted: see the type's remarks.
    /// </remarks>
    public bool AllowLocalSources { get; init; }

    /// <inheritdoc/>
    public Size? Measure(InlineEmbed embed, TextRunProperties properties)
    {
        ArgumentNullException.ThrowIfNull(embed);
        ArgumentNullException.ThrowIfNull(properties);

        if (embed is not ImageEmbed image)
        {
            return null;
        }

        // An explicit size wins, so a resized image stays the size the user dragged it to even
        // once the real pixels are available.
        if (image.Width is { } width && image.Height is { } height)
        {
            return new Size(width, height);
        }

        if (Resolve(image.Source) is not { } bitmap)
        {
            return _fallback.Measure(embed, properties);
        }

        var intrinsic = bitmap.Size;

        if (intrinsic.Width <= 0 || intrinsic.Height <= 0)
        {
            return _fallback.Measure(embed, properties);
        }

        // One dimension given scales the other by the intrinsic aspect ratio, which is what stops
        // an image with only a width from arriving squashed.
        return (image.Width, image.Height) switch
        {
            ({ } w, null) => new Size(w, w * intrinsic.Height / intrinsic.Width),
            (null, { } h) => new Size(h * intrinsic.Width / intrinsic.Height, h),
            _ => intrinsic,
        };
    }

    /// <inheritdoc/>
    public void Draw(DrawingContext context, Rect bounds, InlineEmbed embed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(embed);

        if (embed is not ImageEmbed image || Resolve(image.Source) is not { } bitmap)
        {
            _fallback.Draw(context, bounds, embed);

            return;
        }

        context.DrawImage(bitmap, new Rect(bitmap.Size), bounds);
    }

    /// <summary>Forgets everything decoded so far.</summary>
    /// <remarks>
    /// For a host whose sources can change meaning — a store that reuses keys, a file that was
    /// overwritten. Sources that are <c>data:</c> URLs never need this, since their content is
    /// their name.
    /// </remarks>
    public void Invalidate()
    {
        foreach (var entry in _cache.Values)
        {
            entry?.Dispose();
        }

        _cache.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // The shared instance outlives every control that uses it. A host replacing Embeds and
        // disposing what was there would otherwise turn images off for the whole process.
        if (_disposed || _shared)
        {
            return;
        }

        _disposed = true;

        Invalidate();
    }

    /// <summary>How many sources are currently decoded and held.</summary>
    internal int CachedCount => _cache.Count;

    /// <summary>How many times a source has actually been decoded.</summary>
    /// <remarks>
    /// Exists for the regression test on cache ownership: a source that is resolved twice but
    /// decoded twice is one the cache did not keep, and a bitmap the cache does not keep is one
    /// nothing will ever dispose.
    /// </remarks>
    internal int DecodeCount => _decodes;

    private int _decodes;

    private Bitmap? Resolve(string source)
    {
        if (_disposed || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (_cache.TryGetValue(source, out var cached))
        {
            return cached;
        }

        var bitmap = Decode(source);

        Interlocked.Increment(ref _decodes);

        // Make room rather than handing back a bitmap the cache does not own. An uncached one has
        // no owner and nothing ever disposes it, so a document with more distinct images than the
        // limit would leak one per image per layout pass - in native memory, where it does not
        // even show up as managed pressure.
        while (_cache.Count >= CacheLimit && _cache.Keys.FirstOrDefault() is { } victim)
        {
            if (_cache.TryRemove(victim, out var evicted))
            {
                evicted?.Dispose();
            }
        }

        if (!_cache.TryAdd(source, bitmap))
        {
            bitmap?.Dispose();

            return _cache.TryGetValue(source, out var raced) ? raced : null;
        }

        return bitmap;
    }

    private Bitmap? Decode(string source)
    {
        try
        {
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeDataUri(source);
            }

            // Everything below reaches outside the document. See the type's remarks: by default a
            // pasted document may not name an address the host will then read.
            if (!AllowLocalSources)
            {
                return null;
            }

            if (source.StartsWith("avares:", StringComparison.OrdinalIgnoreCase))
            {
                using var asset = AssetLoader.Open(new Uri(source));

                return Accept(new Bitmap(asset));
            }

            if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                return Accept(new Bitmap(uri.LocalPath));
            }

            // A scheme this does not handle - http, https, ftp - is never resolved, opt-in or not.
            if (source.Contains("://", StringComparison.Ordinal))
            {
                return null;
            }

            return File.Exists(source) ? Accept(new Bitmap(source)) : null;
        }
        catch (Exception)
        {
            // Untrusted input: a truncated data URL, a file that is not an image, a path that
            // cannot be read. One bad image must not stop the document rendering.
            return null;
        }
    }

    /// <summary>Keeps a decoded bitmap, or discards one that is too large to show.</summary>
    private Bitmap? Accept(Bitmap bitmap)
    {
        var pixels = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;

        if (pixels > 0 && pixels <= MaximumPixels)
        {
            return bitmap;
        }

        bitmap.Dispose();

        return null;
    }

    private Bitmap? DecodeDataUri(string source)
    {
        var comma = source.IndexOf(',', StringComparison.Ordinal);

        if (comma < 0)
        {
            return null;
        }

        var header = source.AsSpan(0, comma);

        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Checked before decoding rather than after: base64 is four characters per three bytes, so
        // the length of the payload bounds its size without allocating anything.
        if ((long)(source.Length - comma - 1) / 4 * 3 > MaximumSourceBytes)
        {
            return null;
        }

        var payload = Convert.FromBase64String(source[(comma + 1)..]);

        // A PNG states its dimensions in the IHDR chunk at a fixed offset, so the commonest
        // decompression bomb is refused before anything is allocated for it. Other formats are
        // caught by Accept, after the allocation they were designed to provoke.
        if (IsOversizedPng(payload))
        {
            return null;
        }

        using var stream = new MemoryStream(payload);

        return Accept(new Bitmap(stream));
    }

    private bool IsOversizedPng(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        if (payload.Length < 24 || !payload[..8].SequenceEqual(signature))
        {
            return false;
        }

        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload[16..20]);
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload[20..24]);

        return (long)width * height > MaximumPixels;
    }
}
