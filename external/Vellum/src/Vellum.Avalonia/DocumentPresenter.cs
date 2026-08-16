using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Vellum.Avalonia;

/// <summary>
/// What an editor and a viewer have in common: a document laid out as a stack of blocks,
/// virtualized over a viewport, drawn to a surface.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the two controls differ only in what they add. Presenting a document —
/// flattening it into blocks, reconciling that list against the previous one so an edit relays
/// out only what it touched, keeping a height index so the whole thing can be scrolled without
/// being measured, and realizing the window the viewport covers — is the expensive, subtle,
/// well-tested part, and it is identical for both. The editor adds a caret, a selection, input,
/// an input method, a clipboard and a history; the viewer adds a document property.
/// </para>
/// <para>
/// The alternative was to have <see cref="RichTextViewer"/> host a <see cref="RichTextView"/> and
/// switch its editing off. That was measured and rejected: constructing a
/// <see cref="RichTextView"/> costs 19.7 KiB more than a bare control before it has laid out a
/// single line — a blink timer, an input-method client and a context menu — which is 50% of the
/// allocation of a laid-out view of a clipboard-sized document. A viewer that pays that for
/// machinery it will never use is not a viewer.
/// </para>
/// <para>
/// The seams are deliberately few: <see cref="PresentedDocument"/>, which a subclass must supply;
/// <see cref="Anchor"/> and <see cref="OnReconciled"/>, so a subclass can track a block across a
/// rebuild; <see cref="ScrollAnchorIntoView"/>, so it can keep something on screen; and
/// <see cref="RenderContent"/>, so it can draw under and over the text.
/// </para>
/// </remarks>
public abstract partial class DocumentPresenter : global::Avalonia.Controls.Control
{
    /// <summary>Defines the <see cref="Background"/> property.</summary>
    /// <remarks>
    /// Transparent rather than null, and this matters: Avalonia hit-tests a plain
    /// <see cref="global::Avalonia.Controls.Control"/> against what it actually drew, so a view
    /// that painted only glyphs would not receive a click in the leading above a line, in the gap
    /// below one, or to the right of the last character. Measured: a click 2px from the top of
    /// the control reported the parent <c>ContentPresenter</c> as its source and never reached
    /// the view. Filling the bounds with a transparent brush is what makes the whole control
    /// clickable, and it costs nothing visually.
    /// </remarks>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<DocumentPresenter, IBrush?>(
            nameof(Background), Brushes.Transparent);

    private protected const double DefaultWidth = 400;
    private protected const double CaretWidth = 1;

    /// <summary>The grid lines drawn around table cells.</summary>
    /// <remarks>
    /// A styled property rather than something the paragraph style resolver answers, because it
    /// is not a property of any paragraph: a table's rules belong to the table. Changing it drops
    /// every layout, since the brush is baked into the table views when they are built.
    /// </remarks>
    public static readonly StyledProperty<IBrush> TableBorderBrushProperty =
        AvaloniaProperty.Register<DocumentPresenter, IBrush>(
            nameof(TableBorderBrush), new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD4)));

    /// <summary>The grid lines drawn around table cells.</summary>
    public IBrush TableBorderBrush
    {
        get => GetValue(TableBorderBrushProperty);
        set => SetValue(TableBorderBrushProperty, value);
    }

    /// <summary>The fill drawn behind a header cell, or null to leave header cells plain.</summary>
    /// <remarks>
    /// The control saying what a header row usually looks like. A cell that states its own
    /// background wins, because that came from the document. Changing it drops every layout, for
    /// the same reason <see cref="TableBorderBrushProperty"/> does.
    /// </remarks>
    public static readonly StyledProperty<IBrush?> TableHeaderBrushProperty =
        AvaloniaProperty.Register<DocumentPresenter, IBrush?>(
            nameof(TableHeaderBrush), new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x88, 0x99)));

    /// <summary>The fill drawn behind a header cell, or null to leave header cells plain.</summary>
    public IBrush? TableHeaderBrush
    {
        get => GetValue(TableHeaderBrushProperty);
        set => SetValue(TableHeaderBrushProperty, value);
    }

    /// <summary>The pointer shown over text, shared because a Cursor owns a platform handle.</summary>
    internal static readonly global::Avalonia.Input.Cursor TextCursor =
        new(global::Avalonia.Input.StandardCursorType.Ibeam);

    /// <summary>The pointer shown anywhere that is not text.</summary>
    internal static readonly global::Avalonia.Input.Cursor ArrowCursor =
        new(global::Avalonia.Input.StandardCursorType.Arrow);

    private ITextStyleResolver? _styles;
    private IParagraphStyleResolver _paragraphStyles = new ParagraphStyleResolver();
    private IEmbedRenderer _embeds = BitmapEmbedRenderer.Shared;
    private bool _derivedStyles = true;
    private protected double _width = DefaultWidth;

    /// <summary>The fill behind the whole control.</summary>
    /// <remarks>See <see cref="BackgroundProperty"/> — this is load-bearing for hit testing.</remarks>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Resolves mark sets to run properties.</summary>
    /// <remarks>
    /// <para>
    /// Not called <c>Styles</c>: <see cref="StyledElement.Styles"/> already is.
    /// </para>
    /// <para>
    /// Left unset it is derived from the control's own inherited text properties, so a document
    /// put inside a themed panel picks up that theme's font the way any other control would.
    /// Setting it explicitly opts out of that, and clearing it back to null opts in again.
    /// Either way every layout is discarded, because every run's properties have changed.
    /// </para>
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public ITextStyleResolver TextStyles
    {
        get => _styles ??= BuildStyles();
        set
        {
            _styles = value;
            _derivedStyles = value is null;
            DropLayout();
            InvalidateMeasure();
        }
    }

    /// <summary>Measures and draws inline embeds.</summary>
    /// <remarks>
    /// Defaults to <see cref="BitmapEmbedRenderer.Shared"/>, so images in a pasted or loaded
    /// document appear without the host arranging anything, and a list of two hundred documents
    /// decodes each image once between them rather than once each. Replace it to resolve sources
    /// against an application's own asset store, or with a <see cref="PlaceholderEmbedRenderer"/>
    /// to lay images out without decoding any.
    /// </remarks>
    public IEmbedRenderer Embeds
    {
        get => _embeds;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _embeds = value;
            DropLayout();
            InvalidateMeasure();
        }
    }

    /// <summary>Decides how each paragraph kind is presented.</summary>
    /// <remarks>
    /// Block-level presentation — heading sizes, the bar down a quote, the fill behind code —
    /// as opposed to the character-level presentation <see cref="TextStyles"/> resolves. A host
    /// replaces this to restyle headings without touching the document.
    /// </remarks>
    public IParagraphStyleResolver ParagraphStyles
    {
        get => _paragraphStyles;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _paragraphStyles = value;
            DropLayout();
            InvalidateMeasure();
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // An unconstrained width has no wrap point, so the text would lay out on one endless
        // line and the control would ask for it. A finite fallback keeps that from happening
        // when the control is measured outside a width-constraining parent.
        _width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? DefaultWidth
            : availableSize.Width;

        // An unbounded height means nothing is scrolling this view — it is inside something that
        // will grow to whatever it asks for — so there is no window to virtualize to and every
        // block is realized. See the Scroll part of this class.
        _measureHeight = double.IsInfinity(availableSize.Height) || availableSize.Height <= 0
            ? double.PositiveInfinity
            : availableSize.Height;

        var size = Realize();

        // Room for the caret at the end of the longest line, which otherwise sits exactly on
        // the edge and is clipped to a half-pixel sliver. Paid by the viewer too, so that a
        // document does not reflow by a pixel when it is moved between the two controls.
        return size.WithWidth(size.Width + CaretWidth);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_viewport != finalSize)
        {
            _viewport = finalSize;
            _scrollDirty = true;
        }

        var size = base.ArrangeOverride(finalSize);

        FlushScroll();

        return size;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Background is { } background)
        {
            context.FillRectangle(background, new Rect(Bounds.Size));
        }

        RenderContent(context);
    }

    /// <summary>Draws the realized blocks.</summary>
    /// <remarks>
    /// Called between whatever a subclass draws behind the text and whatever it draws in front.
    /// </remarks>
    /// <param name="context">The surface to draw on.</param>
    private protected void RenderBlocks(DrawingContext context)
    {
        for (var i = _first; i <= _last; i++)
        {
            _views[i].Render(context, BlockOrigin(i));
        }
    }

    /// <summary>Draws everything above the background.</summary>
    /// <remarks>
    /// The default is the text and nothing else, which is exactly a viewer. The editor overrides
    /// this to put the selection highlight underneath and the caret on top.
    /// </remarks>
    /// <param name="context">The surface to draw on.</param>
    private protected virtual void RenderContent(DrawingContext context) => RenderBlocks(context);

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A derived resolver is only as current as the properties it was derived from, so a
        // font change has to invalidate it. An explicitly set one is the caller's to manage.
        if (_derivedStyles && TextProperties.Contains(change.Property))
        {
            _styles = null;
            DropLayout();
            InvalidateMeasure();
        }
        else if (change.Property == TableBorderBrushProperty || change.Property == TableHeaderBrushProperty)
        {
            DropLayout();
            InvalidateMeasure();
        }
    }

    /// <summary>Defines the <see cref="FontFamily"/> property.</summary>
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<DocumentPresenter>();

    /// <summary>Defines the <see cref="FontSize"/> property.</summary>
    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<DocumentPresenter>();

    /// <summary>Defines the <see cref="FontStyle"/> property.</summary>
    public static readonly StyledProperty<FontStyle> FontStyleProperty =
        TextElement.FontStyleProperty.AddOwner<DocumentPresenter>();

    /// <summary>Defines the <see cref="FontWeight"/> property.</summary>
    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<DocumentPresenter>();

    /// <summary>Defines the <see cref="FontStretch"/> property.</summary>
    public static readonly StyledProperty<FontStretch> FontStretchProperty =
        TextElement.FontStretchProperty.AddOwner<DocumentPresenter>();

    /// <summary>Defines the <see cref="Foreground"/> property.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<DocumentPresenter>();

    /// <summary>The font the document is set in, unless a run says otherwise.</summary>
    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>The size the document is set at, unless a run says otherwise.</summary>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>The slant the document is set in, unless a run says otherwise.</summary>
    public FontStyle FontStyle
    {
        get => GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    /// <summary>The weight the document is set in, unless a run says otherwise.</summary>
    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    /// <summary>The width the document is set in, unless a run says otherwise.</summary>
    public FontStretch FontStretch
    {
        get => GetValue(FontStretchProperty);
        set => SetValue(FontStretchProperty, value);
    }

    /// <summary>The colour the document is drawn in, unless a run says otherwise.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>
    /// The properties a derived style resolver is built from, so a change to any of them has to
    /// throw it away.
    /// </summary>
    /// <remarks>
    /// These are the <see cref="TextElement"/> properties themselves, not the ones re-declared
    /// above: <c>AddOwner</c> registers an existing property against a new owner and hands back
    /// the same instance, so the two are one property and comparing against either works.
    /// </remarks>
    private static readonly HashSet<AvaloniaProperty> TextProperties =
    [
        TextElement.FontFamilyProperty,
        TextElement.FontSizeProperty,
        TextElement.FontStyleProperty,
        TextElement.FontWeightProperty,
        TextElement.FontStretchProperty,
        TextElement.ForegroundProperty,
    ];

    private ITextStyleResolver BuildStyles()
    {
        _derivedStyles = true;

        return new TextStyleResolver(
            new Typeface(
                TextElement.GetFontFamily(this),
                TextElement.GetFontStyle(this),
                TextElement.GetFontWeight(this),
                TextElement.GetFontStretch(this)),
            TextElement.GetFontSize(this),
            TextElement.GetForeground(this) ?? Brushes.Black);
    }
}
