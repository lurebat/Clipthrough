using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Vellum.Avalonia;

/// <summary>
/// Scrolling, and the virtualization that hangs off it, per architecture 4.6.
/// </summary>
/// <remarks>
/// <para>
/// The view scrolls itself rather than being scrolled. A control inside an ordinary
/// <see cref="ScrollViewer"/> is measured with an unbounded height, lays out everything it has,
/// and is then slid about under a clip — which is fine for a page of text and hopeless for a
/// document of ten thousand paragraphs, because the cost is paid before a single line is seen.
/// Implementing <see cref="ILogicalScrollable"/> inverts that: the scroll viewer hands the view
/// a viewport and an offset and asks it to draw what belongs there, so only the blocks on screen
/// are ever laid out.
/// </para>
/// <para>
/// <b>The view has to be the scroll viewer's direct content for any of this to happen.</b>
/// <c>ScrollContentPresenter</c> looks at its immediate child and nothing deeper, so a view
/// wrapped in a <see cref="Border"/> for a page effect is scrolled the ordinary way and lays
/// everything out. That is not a failure — it is the fallback below, and it stays correct — but
/// it is not virtualized. <see cref="RichTextEditor"/>'s template gets the nesting right.
/// </para>
/// <para>
/// The fallback matters as much as the fast path. Measured with an unbounded height there is no
/// window to virtualize to, so every block is realized and the view reports its full size, which
/// is exactly what it did before this file existed.
/// </para>
/// <para>
/// Coordinates: the control's own space is the <em>viewport</em>, and the block stack lives in
/// document space starting at zero. The only two places the two meet are
/// <see cref="BlockOrigin"/>, which subtracts the offset, and <see cref="BlockIndexAt"/>, which
/// adds it. Everything else composes those and needed no change.
/// </para>
/// </remarks>
public abstract partial class DocumentPresenter : ILogicalScrollable
{
    /// <summary>How much content is realized beyond each edge of the viewport, in pixels.</summary>
    /// <remarks>
    /// Enough that a small scroll lands on blocks that are already laid out. Too little and a
    /// wheel notch lays out a paragraph in the middle of the gesture; too much and the point of
    /// virtualizing is diluted.
    /// </remarks>
    private const double Overscan = 200;

    private protected Vector _offset;
    private protected Size _viewport;
    private Size _extent;
    private bool _canHorizontallyScroll;
    private bool _canVerticallyScroll = true;
    private protected bool _scrollDirty;
    private protected bool _pendingAnchor;
    private protected double _measureHeight = double.PositiveInfinity;
    private double _realizedWidth = double.NaN;
    private protected int _first;
    private protected int _last = -1;
    private EventHandler? _scrollInvalidated;

    /// <summary>The size of the whole document, measured blocks and estimated ones together.</summary>
    /// <remarks>
    /// Not a fixed number: a block nobody has laid out contributes an estimate, and measuring it
    /// replaces that estimate with the truth. The total therefore moves as the document is
    /// explored, which is inherent to virtualizing over content of unknown height.
    /// </remarks>
    public Size Extent => _extent;

    /// <summary>How far the viewport has been scrolled into the document.</summary>
    /// <remarks>
    /// Clamped to the document on the way in, so a host may set it freely. Setting it schedules
    /// a layout pass, which is what moves the realized window.
    /// </remarks>
    public Vector Offset
    {
        get => _offset;
        set => ApplyOffset(value, relayout: true);
    }

    /// <summary>The part of the document on screen.</summary>
    public Size Viewport => _viewport;

    /// <summary>Whether the host will scroll this view horizontally.</summary>
    /// <remarks>
    /// Set by the scroll viewer, not by the application. The view wraps to the width it is
    /// given, so there is normally nothing to scroll to and this stays false.
    /// </remarks>
    public bool CanHorizontallyScroll
    {
        get => _canHorizontallyScroll;
        set
        {
            if (_canHorizontallyScroll != value)
            {
                _canHorizontallyScroll = value;
                InvalidateMeasure();
            }
        }
    }

    /// <summary>Whether the host will scroll this view vertically.</summary>
    /// <remarks>Set by the scroll viewer, not by the application.</remarks>
    public bool CanVerticallyScroll
    {
        get => _canVerticallyScroll;
        set
        {
            if (_canVerticallyScroll != value)
            {
                _canVerticallyScroll = value;
                InvalidateMeasure();
            }
        }
    }

    /// <summary>The blocks that have been laid out, as a half-open range.</summary>
    /// <remarks>
    /// For tests and diagnostics: this is the evidence that virtualization is happening at all.
    /// Empty when nothing has been realized yet.
    /// </remarks>
    public Range RealizedBlocks => _last < _first ? new Range(0, 0) : new Range(_first, _last + 1);

    bool ILogicalScrollable.IsLogicalScrollEnabled => true;

    /// <summary>The distance one wheel notch moves, in pixels.</summary>
    /// <remarks>
    /// Three blocks' worth of the height a block in this document tends to be, rather than the
    /// fixed fifty pixels a non-logical scroller uses, so a document set in a large face scrolls
    /// at the same rate in lines as one set in a small face.
    /// </remarks>
    Size ILogicalScrollable.ScrollSize => new(1, Math.Max(1, _heights.Estimate) * 3);

    Size ILogicalScrollable.PageScrollSize => new(_viewport.Width, _viewport.Height);

    event EventHandler? ILogicalScrollable.ScrollInvalidated
    {
        add => _scrollInvalidated += value;
        remove => _scrollInvalidated -= value;
    }

    void ILogicalScrollable.RaiseScrollInvalidated(EventArgs e) => _scrollInvalidated?.Invoke(this, e);

    /// <remarks>
    /// The view has no child controls, so there is never a descendant to bring into view. What a
    /// caller actually wants scrolled to — the editor's caret — the presenter does itself
    /// whenever the document changes; see <see cref="ScrollAnchorIntoView"/>.
    /// </remarks>
    bool ILogicalScrollable.BringIntoView(Control target, Rect targetRect) => false;

    /// <remarks>
    /// Focus never moves <em>within</em> the view — it is one control holding a document, not a
    /// list of focusable items — so there is no next control in any direction.
    /// </remarks>
    Control? ILogicalScrollable.GetControlInDirection(NavigationDirection direction, Control? from) => null;

    /// <summary>
    /// The viewport height the realized window is computed against.
    /// </summary>
    /// <remarks>
    /// Taken from the measure constraint rather than from <see cref="Viewport"/>, which is only
    /// known after arrange. Using the constraint keeps measure self-contained: it cannot depend
    /// on a value produced by the pass that follows it, so there is no way for the two to chase
    /// each other. Infinite means nothing is scrolling the view and every block is realized.
    /// </remarks>
    private protected double ViewportHeight => _measureHeight;

    /// <summary>Whether the view is laying out a window rather than the whole document.</summary>
    private protected bool IsVirtualizing => !double.IsInfinity(_measureHeight) && _canVerticallyScroll;

    /// <summary>Records a new extent, telling the scroll viewer if it moved.</summary>
    private void SetExtent(Size value)
    {
        if (_extent != value)
        {
            _extent = value;
            _scrollDirty = true;
        }
    }

    /// <summary>
    /// Moves the viewport, clamped to the document.
    /// </summary>
    /// <param name="value">Where to move it to.</param>
    /// <param name="relayout">
    /// Whether the realized window has to be recomputed. False when the caller has just realized
    /// a window and is only correcting for heights that turned out different from their
    /// estimates: the same content is on screen, so laying it out again would be work for
    /// nothing — and, done from inside the measure pass, an invalidation that measures again.
    /// </param>
    private protected void ApplyOffset(Vector value, bool relayout)
    {
        var clamped = new Vector(
            Math.Clamp(value.X, 0, Math.Max(0, _extent.Width - _viewport.Width)),
            Math.Clamp(value.Y, 0, Math.Max(0, _extent.Height - _viewport.Height)));

        if (clamped == _offset)
        {
            return;
        }

        _offset = clamped;
        _scrollDirty = true;

        if (relayout)
        {
            InvalidateMeasure();
        }

        InvalidateVisual();
    }

    /// <summary>Tells the scroll viewer that the extent, the offset or the viewport has moved.</summary>
    /// <remarks>
    /// Raised from arrange rather than from each of the setters. The scroll viewer answers by
    /// reading all three back and writing them into its own properties, which invalidates layout;
    /// doing that in the middle of a measure pass is how a control gets stuck laying itself out
    /// forever.
    /// </remarks>
    private protected void FlushScroll()
    {
        if (!_scrollDirty)
        {
            return;
        }

        _scrollDirty = false;
        _scrollInvalidated?.Invoke(this, EventArgs.Empty);
    }
}
