using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Vellum.Avalonia;

/// <summary>
/// Selecting an image and resizing it by dragging a corner, per architecture 10.3 P7.
/// </summary>
/// <remarks>
/// <para>
/// An image is "selected" when the selection covers exactly the one position it occupies. That is
/// deliberately not a new selection kind: a block image is a leaf, so
/// <see cref="NodeSelection.At"/> answers for it, while an inline image lives inside a paragraph
/// where a node selection cannot reach and is covered by a one-long <see cref="TextSelection"/>.
/// Deriving the state from the range rather than from the kind means both arrive here by the same
/// path, and Delete already removes either without knowing an image was involved.
/// </para>
/// <para>
/// The drag draws an outline and applies nothing until the pointer is released. Applying a
/// transaction per pointer move would put a hundred steps in the undo stack for one gesture, and
/// would reflow the document under the pointer while the user is still aiming.
/// </para>
/// </remarks>
public partial class RichTextView
{
    /// <summary>Defines the <see cref="ImageHandleBrush"/> property.</summary>
    public static readonly StyledProperty<IBrush?> ImageHandleBrushProperty =
        AvaloniaProperty.Register<RichTextView, IBrush?>(
            nameof(ImageHandleBrush), Brushes.White);

    /// <summary>Defines the <see cref="ImageHandleBorderBrush"/> property.</summary>
    public static readonly StyledProperty<IBrush?> ImageHandleBorderBrushProperty =
        AvaloniaProperty.Register<RichTextView, IBrush?>(
            nameof(ImageHandleBorderBrush), new SolidColorBrush(Color.FromRgb(0, 120, 215)));

    /// <summary>The side of a resize handle, in device-independent pixels.</summary>
    public const double ImageHandleSize = 8;

    /// <summary>The smallest side a drag may leave an image at.</summary>
    /// <remarks>
    /// A drag past the opposite corner would otherwise produce a zero or negative dimension, which
    /// <see cref="ImageEmbed"/> refuses outright — so without a floor the gesture throws rather
    /// than doing something sensible.
    /// </remarks>
    public const double MinimumImageSide = 8;

    /// <summary>The cursor for each corner handle, in the same order as <see cref="HandlesOf"/>.</summary>
    internal static readonly Cursor[] HandleCursors =
    [
        new(StandardCursorType.TopLeftCorner),
        new(StandardCursorType.TopRightCorner),
        new(StandardCursorType.BottomRightCorner),
        new(StandardCursorType.BottomLeftCorner),
    ];

    /// <summary>The image being dragged, or null.</summary>
    private int? _resizing;

    /// <summary>The corner being dragged: 0 top-left, then clockwise.</summary>
    private int _resizeHandle;

    /// <summary>The image's box when the drag began.</summary>
    private Rect _resizeFrom;

    /// <summary>Where the box would land if the pointer were released now.</summary>
    private Rect _resizeTo;

    /// <summary>The fill of a resize handle.</summary>
    public IBrush? ImageHandleBrush
    {
        get => GetValue(ImageHandleBrushProperty);
        set => SetValue(ImageHandleBrushProperty, value);
    }

    /// <summary>The outline of a resize handle, and of the box drawn while dragging.</summary>
    public IBrush? ImageHandleBorderBrush
    {
        get => GetValue(ImageHandleBorderBrushProperty);
        set => SetValue(ImageHandleBorderBrushProperty, value);
    }

    /// <summary>The position of the image the selection covers, or null if it covers anything else.</summary>
    public int? SelectedImage
    {
        get
        {
            var selection = _state.Selection;

            // A rectangle's bounds happen to be one apart when it is a single cell wide, and
            // the position they start at is not an image but the cell boundary.
            return selection is not CellSelection
                && selection.To - selection.From == 1
                && ImageAt(selection.From) is not null
                ? selection.From
                : null;
        }
    }

    /// <summary>Whether an image is being resized by a pointer drag.</summary>
    public bool IsResizingImage => _resizing is not null;

    /// <summary>Selects the image at a position.</summary>
    /// <param name="position">The position the image occupies.</param>
    /// <returns>Whether there was an image there.</returns>
    /// <remarks>
    /// A block image becomes a <see cref="NodeSelection"/> and an inline one a one-long
    /// <see cref="TextSelection"/>, because a node selection cannot address a position inside a
    /// paragraph. Both satisfy <see cref="SelectedImage"/>.
    /// </remarks>
    public bool SelectImage(int position)
    {
        if (ImageAt(position) is null)
        {
            return false;
        }

        var selection = NodeSelection.At(_state.Doc, position)
            ?? (Selection)new TextSelection(position, position + 1);

        // Installed directly rather than through a transaction, as every other selection change
        // is: moving the selection does not change the document and has no business on the undo
        // stack. Transaction.Apply would refuse it anyway, since nothing changed.
        State = EditorState.Create(_state.Doc, selection);
        ResetCaretBlink();

        return true;
    }

    /// <summary>Resizes the selected image.</summary>
    /// <param name="size">The size to give it, in device-independent pixels.</param>
    /// <returns>Whether an image was resized.</returns>
    /// <remarks>
    /// Public so that a host can drive a size from a properties panel rather than only from a
    /// drag. Both sides are floored at <see cref="MinimumImageSide"/>; a non-finite size is
    /// refused rather than written into the document, because it would poison every later layout.
    /// </remarks>
    public bool ResizeSelectedImage(Size size)
    {
        if (SelectedImage is not { } position
            || !double.IsFinite(size.Width)
            || !double.IsFinite(size.Height))
        {
            return false;
        }

        var width = Math.Max(MinimumImageSide, size.Width);
        var height = Math.Max(MinimumImageSide, size.Height);
        var replacement = Resized(position, width, height);

        if (replacement is null)
        {
            return false;
        }

        var transaction = _state.Transaction()
            .Replace(position, position + 1, replacement)
            .As(TransactionKind.Structure);

        // No SetSelection: replacing exactly the one position the image occupies maps the
        // selection onto the new image unchanged, in both the node and the text shape. Setting it
        // explicitly was measured to make no difference to any test and was removed rather than
        // left as an untested branch.
        return transaction.Failures.IsEmpty && Apply(transaction);
    }

    /// <summary>The image at a document position, or null if there is not one there.</summary>
    private ImageEmbed? ImageAt(int position)
    {
        if (position < 0 || position >= _state.Doc.ContentSize)
        {
            return null;
        }

        var at = _state.Doc.Resolve(position);

        if (at.Paragraph is { } paragraph)
        {
            return paragraph.Content.TryGetEmbedAt(at.ParentOffset, out var embed)
                ? embed as ImageEmbed
                : null;
        }

        return (at.NodeAfter as BlockImageNode)?.Image;
    }

    /// <summary>The slice that puts a resized copy of the image at a position.</summary>
    private Slice? Resized(int position, double width, double height)
    {
        var at = _state.Doc.Resolve(position);

        if (at.Paragraph is { } paragraph)
        {
            if (!paragraph.Content.TryGetEmbedAt(at.ParentOffset, out var embed)
                || embed is not ImageEmbed image)
            {
                return null;
            }

            return Slice.OfInline(InlineContent.FromEmbed(
                image with { Width = width, Height = height },
                paragraph.Content.MarkAt(at.ParentOffset)));
        }

        return at.NodeAfter is BlockImageNode block
            ? Slice.OfBlocks(new BlockImageNode(
                block.Image with { Width = width, Height = height }, block.Align))
            : null;
    }

    /// <summary>The image whose box contains a point, or null.</summary>
    /// <remarks>
    /// Searched from the position the point hit-tests to and the one before it, because a click on
    /// an inline image reports the position at whichever of its edges is nearer.
    /// </remarks>
    private int? ImageUnder(Point point)
    {
        var hit = PositionAt(point);

        foreach (var candidate in (int[])[hit, hit - 1])
        {
            if (ImageAt(candidate) is not null
                && ImageRect(candidate) is { } rect
                && rect.Contains(point))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The four corner handles of the selected image, or nothing.</summary>
    private Rect[] Handles()
    {
        if (SelectedImage is not { } position || ImageRect(position) is not { } rect)
        {
            return [];
        }

        return HandlesOf(rect);
    }

    private static Rect[] HandlesOf(Rect rect)
    {
        var half = ImageHandleSize / 2;

        return
        [
            new Rect(rect.Left - half, rect.Top - half, ImageHandleSize, ImageHandleSize),
            new Rect(rect.Right - half, rect.Top - half, ImageHandleSize, ImageHandleSize),
            new Rect(rect.Right - half, rect.Bottom - half, ImageHandleSize, ImageHandleSize),
            new Rect(rect.Left - half, rect.Bottom - half, ImageHandleSize, ImageHandleSize),
        ];
    }

    /// <summary>The handle under a point, or null.</summary>
    private int? HandleUnder(Point point)
    {
        var handles = Handles();

        for (var i = 0; i < handles.Length; i++)
        {
            if (handles[i].Contains(point))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Starts a resize if the pointer went down on a handle.</summary>
    private bool TryBeginResize(Point point)
    {
        if (SelectedImage is not { } position
            || HandleUnder(point) is not { } handle
            || ImageRect(position) is not { } rect)
        {
            return false;
        }

        _resizing = position;
        _resizeHandle = handle;
        _resizeFrom = rect;
        _resizeTo = rect;

        InvalidateVisual();

        return true;
    }

    /// <summary>Moves the box a resize would leave behind.</summary>
    /// <remarks>
    /// <para>
    /// The corner opposite the one being dragged is the anchor and does not move, and the aspect
    /// ratio is kept: a corner drag that could change it independently on both axes distorts the
    /// picture for a gesture nobody makes deliberately.
    /// </para>
    /// <para>
    /// The distances are signed, and a pointer dragged past the anchor gives zero rather than its
    /// distance beyond it. Taking the magnitude instead would mirror the box, so dragging a corner
    /// well past the opposite one would make the image <em>larger</em> the further it was pulled
    /// the wrong way.
    /// </para>
    /// </remarks>
    private void UpdateResize(Point point)
    {
        if (_resizing is null)
        {
            return;
        }

        var anchor = Opposite(_resizeFrom, _resizeHandle);
        var width = Math.Max(0, _resizeHandle is 0 or 3
            ? anchor.X - point.X
            : point.X - anchor.X);
        var height = Math.Max(0, _resizeHandle is 0 or 1
            ? anchor.Y - point.Y
            : point.Y - anchor.Y);

        // The axis the pointer moved furthest along wins, so the box follows the pointer on one
        // axis exactly and trails it on the other rather than shearing. A zero-sided box has no
        // ratio to keep, so it is grown by the pointer's distance directly.
        var scale = _resizeFrom.Width > 0 && _resizeFrom.Height > 0
            ? Math.Max(width / _resizeFrom.Width, height / _resizeFrom.Height)
            : 1;
        var wanted = _resizeFrom.Width > 0 && _resizeFrom.Height > 0
            ? new Size(_resizeFrom.Width * scale, _resizeFrom.Height * scale)
            : new Size(width, height);

        wanted = new Size(
            Math.Max(MinimumImageSide, wanted.Width),
            Math.Max(MinimumImageSide, wanted.Height));

        _resizeTo = new Rect(
            new Point(
                _resizeHandle is 0 or 3 ? anchor.X - wanted.Width : anchor.X,
                _resizeHandle is 0 or 1 ? anchor.Y - wanted.Height : anchor.Y),
            wanted);

        InvalidateVisual();
    }

    /// <summary>Writes the drag into the document, if there was one.</summary>
    private void EndResize()
    {
        if (_resizing is null)
        {
            return;
        }

        var size = _resizeTo.Size;

        _resizing = null;

        // A press with no movement is a click on a handle, not a resize, and must not put a
        // transaction on the undo stack.
        if (size != _resizeFrom.Size)
        {
            ResizeSelectedImage(size);
        }

        InvalidateVisual();
    }

    /// <summary>The corner a drag of <paramref name="handle"/> pivots around.</summary>
    private static Point Opposite(Rect rect, int handle) => handle switch
    {
        0 => rect.BottomRight,
        1 => rect.BottomLeft,
        2 => rect.TopLeft,
        _ => rect.TopRight,
    };

    /// <summary>The cursor for a point, or null if the point is not on a handle.</summary>
    private Cursor? ResizeCursor(Point point) =>
        HandleUnder(point) is { } handle ? HandleCursors[handle] : null;

    private void RenderImageHandles(DrawingContext context)
    {
        if (SelectedImage is not { } position || ImageRect(position) is not { } rect)
        {
            return;
        }

        var border = ImageHandleBorderBrush;
        var pen = border is null ? null : new ImmutablePen(border.ToImmutable(), 1);

        if (_resizing is not null)
        {
            // The box the drag would leave, so the user is aiming at the result rather than at
            // the pointer. Only an outline: filling it would hide the picture being resized.
            context.DrawRectangle(null, pen, _resizeTo);
            rect = _resizeTo;
        }

        foreach (var handle in HandlesOf(rect))
        {
            context.DrawRectangle(ImageHandleBrush, pen, handle);
        }
    }
}
