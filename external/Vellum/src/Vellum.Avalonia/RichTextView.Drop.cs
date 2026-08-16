using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Vellum.Avalonia;

/// <summary>
/// Getting images and documents into the editor from outside it: pasted bitmaps, and drops.
/// </summary>
/// <remarks>
/// <para>
/// A drop and a paste are the same problem wearing different clothes — a transfer from another
/// application holding several flavours of one thing — so both end up in
/// <see cref="RichTextClipboard.ReadAsync(IAsyncDataTransfer, IEnumerable{IDocumentImporter})"/>
/// and differ only in where the caret goes first.
/// </para>
/// <para>
/// Images are the exception, because the model holds a reference and a transfer holds pixels.
/// <see cref="ImageStore"/> bridges the two, and defaults to keeping the bytes inside the document
/// so this works without the host arranging anything.
/// </para>
/// </remarks>
public partial class RichTextView
{
    private IImageStore _imageStore = new DataUriImageStore();

    /// <summary>Where the bytes of a pasted or dropped image are kept.</summary>
    /// <remarks>
    /// Defaults to a <see cref="DataUriImageStore"/>, which carries them inside the document.
    /// Replace it to write them to an application's own store instead — and note that whatever
    /// <see cref="DocumentPresenter.Embeds"/> is set to has to resolve what this returns, which
    /// for anything other than a <c>data:</c> URL means configuring that as well.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public IImageStore ImageStore
    {
        get => _imageStore;
        set => _imageStore = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Whether a drop onto the editor inserts what was dropped.</summary>
    /// <remarks>
    /// On by default. A read-only view refuses regardless, because a drop is an edit.
    /// </remarks>
    public bool AcceptsDrop { get; set; } = true;

    /// <summary>Why an image could not be inserted.</summary>
    /// <param name="byteCount">How large it was.</param>
    /// <param name="mediaType">What kind of image it was.</param>
    public sealed class ImageRefusedEventArgs(int byteCount, string mediaType) : EventArgs
    {
        /// <summary>How large the refused image was, in bytes.</summary>
        public int ByteCount { get; } = byteCount;

        /// <summary>The media type it arrived as.</summary>
        public string MediaType { get; } = mediaType;
    }

    /// <summary>Raised when <see cref="ImageStore"/> declined an image.</summary>
    /// <remarks>
    /// <para>
    /// Pasting a photograph into an editor whose store caps at four megabytes does nothing at all:
    /// no image, no error, no clue. The refusal is right — better no image than one that cannot be
    /// drawn — but silence about it is not, because from the user's side an editor that ignores
    /// Ctrl+V is indistinguishable from one that is broken.
    /// </para>
    /// <para>
    /// A host that does not subscribe gets the old behaviour, so this costs nothing to ignore.
    /// </para>
    /// </remarks>
    public event EventHandler<ImageRefusedEventArgs>? ImageRefused;

    /// <summary>Inserts an image at the caret, replacing the selection.</summary>
    /// <param name="bytes">The image, in whatever format it arrived in.</param>
    /// <param name="mediaType">Its media type, such as <c>image/png</c>.</param>
    /// <returns><see langword="true"/> if the document changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mediaType"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Leaves the document alone when <see cref="ImageStore"/> declines the bytes — too large,
    /// wrong type, a store that is full — and raises <see cref="ImageRefused"/> so the host can
    /// say so. That is a refusal rather than a failure: better a document without the image than
    /// one carrying an embed that resolves to nothing.
    /// </remarks>
    public bool InsertImage(ReadOnlySpan<byte> bytes, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        if (ImageStore.Save(bytes, mediaType) is not { } source)
        {
            ImageRefused?.Invoke(this, new ImageRefusedEventArgs(bytes.Length, mediaType));

            return false;
        }

        return PasteDocument(new DocumentNode([
            new ParagraphNode(InlineContent.FromEmbed(new ImageEmbed(source))),
        ]));
    }

    /// <summary>Reads an image out of a transfer whose contents are already in hand.</summary>
    private async Task<bool> InsertImageFrom(IDataTransfer data)
    {
        if (data.TryGetBitmap() is { } bitmap)
        {
            using (bitmap)
            {
                if (Encode(bitmap) is { } png)
                {
                    return InsertImage(png, "image/png");
                }
            }
        }

        return data.TryGetFiles() is { } files && await InsertImageFiles(files).ConfigureAwait(true);
    }

    /// <summary>Reads an image out of a transfer that has to be awaited, as a clipboard's is.</summary>
    private async Task<bool> InsertImageFrom(IAsyncDataTransfer data)
    {
        if (await data.TryGetBitmapAsync().ConfigureAwait(true) is { } bitmap)
        {
            using (bitmap)
            {
                if (Encode(bitmap) is { } png)
                {
                    return InsertImage(png, "image/png");
                }
            }
        }

        return await data.TryGetFilesAsync().ConfigureAwait(true) is { } files
            && await InsertImageFiles(files).ConfigureAwait(true);
    }

    /// <summary>Inserts every image among some dropped or pasted files.</summary>
    private async Task<bool> InsertImageFiles(IEnumerable<IStorageItem> files)
    {
        var inserted = false;

        foreach (var file in files.OfType<IStorageFile>())
        {
            if (MediaTypeOf(file.Name) is not { } mediaType)
            {
                continue;
            }

            try
            {
                using var stream = await file.OpenReadAsync().ConfigureAwait(true);
                using var buffer = new MemoryStream();

                await stream.CopyToAsync(buffer).ConfigureAwait(true);

                inserted |= InsertImage(buffer.ToArray(), mediaType);
            }
            catch (Exception)
            {
                // A file the user dropped that this process cannot read — gone, locked, or on a
                // volume that went away. Skip it; the rest of the drop is still worth having.
            }
        }

        return inserted;
    }

    /// <summary>Re-encodes a bitmap as PNG, since a transfer hands over decoded pixels.</summary>
    private static byte[]? Encode(Bitmap bitmap)
    {
        try
        {
            using var buffer = new MemoryStream();

            bitmap.Save(buffer, new PngBitmapEncoderOptions());

            return buffer.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The media type a filename implies, or null when it is not an image.</summary>
    /// <remarks>
    /// By extension rather than by sniffing, because the answer decides only what the <c>data:</c>
    /// URL announces — the decoder works it out from the bytes regardless. Anything that is not an
    /// image is left alone: dropping a spreadsheet on a text editor should do nothing rather than
    /// insert its bytes.
    /// </remarks>
    internal static string? MediaTypeOf(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => null,
        };

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanAcceptDrop() ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!CanAcceptDrop())
        {
            e.DragEffects = DragDropEffects.None;

            return;
        }

        e.Handled = true;

        // The caret moves to the pointer before anything is inserted, so a drop lands where it was
        // aimed rather than wherever the caret happened to have been left.
        MoveTo(PositionAt(e.GetPosition(this)), extend: false);

        try
        {
            if (await InsertImageFrom(e.DataTransfer).ConfigureAwait(true))
            {
                return;
            }

            if (RichTextClipboard.Read(e.DataTransfer, ReadableFormats())
                is { Doc.Blocks.IsEmpty: false } result)
            {
                PasteDocument(result.Doc);
            }
        }
        catch (Exception)
        {
            // async void: nothing above this can catch, and a drop from a misbehaving source must
            // not take the application down.
        }
    }

    private bool CanAcceptDrop() => AcceptsDrop && !IsReadOnly;
}
