using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Vellum.Avalonia;

/// <summary>
/// The view of one <see cref="ParagraphNode"/>, per architecture 4.6.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the run cache is driven.</b> Architecture 4.6 originally sketched
/// <c>_runCache.InvalidateFrom(localOffset)</c> on an edit. Increment 0 measured that and it is
/// wrong: invalidation is entry-granular and a paragraph is a single entry keyed 0, so
/// <c>InvalidateFrom(k)</c> for any k greater than zero returns the <em>pre-edit</em> layout
/// without ever asking the source for a run. The measurement is in
/// <c>docs/increment-0-findings.md</c> §2 and architecture §2.3 records the reversal.
/// </para>
/// <para>
/// So the cache is driven by <em>lifetime, not offset</em>. Content change means the whole
/// cache goes; a width-only change keeps it, which is where the cache actually earns its place
/// — Increment 0 measured that case at roughly 3.5× and it is the case that fires on every
/// window resize, once per paragraph.
/// </para>
/// </remarks>
public sealed class ParagraphView : BlockView
{
    private readonly ITextStyleResolver _base;
    private readonly IParagraphStyleResolver _kinds;
    private readonly IEmbedRenderer _embeds;
    private readonly double _indentStep;
    private ITextStyleResolver _styles;
    private ParagraphStyle _style;
    private TextRunCache _runCache = new();
    private ParagraphNode _paragraph;
    private Preedit? _preedit;
    private TextLayout? _layout;
    private TextLayout? _markerLayout;
    private ListMarker? _marker;
    private int _depth;
    private double _layoutWidth = double.NaN;

    /// <summary>Creates a view of a paragraph.</summary>
    /// <param name="paragraph">The paragraph to present.</param>
    /// <param name="styles">Resolves mark sets to run properties.</param>
    /// <param name="embeds">Measures and draws embeds, or null for the placeholder renderer.</param>
    /// <param name="kinds">Decides how the paragraph's kind is presented, or null for the default.</param>
    /// <param name="indentStep">Device pixels per level of <see cref="ParagraphNode.IndentLevel"/>.</param>
    public ParagraphView(
        ParagraphNode paragraph,
        ITextStyleResolver styles,
        IEmbedRenderer? embeds = null,
        IParagraphStyleResolver? kinds = null,
        double indentStep = DefaultIndentStep)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        ArgumentNullException.ThrowIfNull(styles);

        if (indentStep < 0 || double.IsNaN(indentStep))
        {
            throw new ArgumentOutOfRangeException(
                nameof(indentStep), indentStep, "An indent step cannot be negative.");
        }

        _paragraph = paragraph;
        _base = styles;
        _kinds = kinds ?? new ParagraphStyleResolver();
        _embeds = embeds ?? new PlaceholderEmbedRenderer();
        _indentStep = indentStep;
        _style = _kinds.Resolve(paragraph.Kind);
        _styles = KindStyles.Wrap(styles, _style);
    }

    /// <summary>The default width of one indent step, in device pixels.</summary>
    public const double DefaultIndentStep = 24;

    /// <summary>
    /// How far a block fill's corners are rounded. Small on purpose: enough to stop a code
    /// block reading as a raw scanline, not enough to make it a card.
    /// </summary>
    private const double BackgroundCornerRadius = 4;

    /// <summary>The paragraph currently presented.</summary>
    public ParagraphNode Paragraph => _paragraph;

    /// <summary>
    /// How far the paragraph's whole text block is inset from the left, from its indent level.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>TextParagraphProperties.Indent</c>, which indents only the first
    /// line of a paragraph. An indent level insets the entire block, so it is applied as an
    /// offset of the layout: the layout wraps to a narrower width and every coordinate crossing
    /// this type's boundary is shifted by it.
    /// </remarks>
    public double Indent => ((_paragraph.IndentLevel + _depth) * _indentStep) + _style.LeftInset;

    /// <summary>How deep in a list the paragraph sits; zero when it is not in one.</summary>
    public int Depth => _depth;

    /// <summary>The bullet or number drawn in front of the paragraph, or null for none.</summary>
    public ListMarker? Marker => _marker;

    /// <summary>
    /// Tells the view where in a list its paragraph sits and what to draw in front of it.
    /// </summary>
    /// <remarks>
    /// Not part of the paragraph, which is why it is set rather than read. Nothing in a
    /// paragraph node says what list encloses it or which number it takes, and both can change
    /// without the paragraph changing at all: deleting the first item of a list renumbers every
    /// item after it while leaving their paragraphs untouched, references and all.
    /// </remarks>
    /// <param name="depth">How many list levels enclose the paragraph.</param>
    /// <param name="marker">The bullet or number to draw, or null for none.</param>
    /// <returns>True if anything changed and the caller must redraw.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is negative.</exception>
    public bool SetLead(int depth, ListMarker? marker)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);

        if (depth == _depth && marker.Equals(_marker))
        {
            return false;
        }

        _depth = depth;
        _marker = marker;
        _markerLayout = null;

        // The indent is part of the width the text wraps to, so the layout is stale.
        _layout = null;

        return true;
    }

    /// <summary>Where the text sits inside the block, past the indent and the space above.</summary>
    private Point Origin => new(Indent, _style.SpaceBefore + _style.Padding.Top);

    /// <inheritdoc/>
    public override int ContentSize => _paragraph.ContentSize;

    /// <inheritdoc/>
    public override Size Size =>
        _layout is null ? default : new Size(
            Indent + _layout.Width + _style.Padding.Right,
            _style.SpaceBefore + _style.Padding.Top
                + _layout.Height
                + _style.Padding.Bottom + _style.SpaceAfter);

    /// <summary>
    /// The current layout, laying it out at the last measured width if it is stale.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view has never been measured.</exception>
    public TextLayout Layout => _layout ?? throw new InvalidOperationException(
        "The paragraph has not been measured yet; call Measure before asking for geometry.");

    /// <summary>
    /// Points the view at a new paragraph, discarding the layout if the content differs.
    /// </summary>
    /// <remarks>
    /// Attributes that only affect how existing text is arranged — alignment, indent — still
    /// force a relayout, but they keep the run cache, because the shaped runs themselves are
    /// unaffected. That is the same reasoning as a width change.
    /// </remarks>
    /// <param name="paragraph">The new paragraph.</param>
    /// <returns>True if anything changed and the caller must redraw.</returns>
    public bool Update(ParagraphNode paragraph)
    {
        ArgumentNullException.ThrowIfNull(paragraph);

        if (ReferenceEquals(paragraph, _paragraph) || paragraph.Equals(_paragraph))
        {
            return false;
        }

        var textChanged = !paragraph.Content.Equals(_paragraph.Content);
        var kindChanged = paragraph.Kind != _paragraph.Kind;

        _paragraph = paragraph;
        _layout = null;

        if (kindChanged)
        {
            // A kind change is a font change — a different size, weight or family for every run
            // in the paragraph — so the shaped runs are worthless and the cache goes with them.
            _style = _kinds.Resolve(paragraph.Kind);
            _styles = KindStyles.Wrap(_base, _style);
            _runCache = new TextRunCache();

            // The marker is drawn in the paragraph's own font, which the kind just changed.
            _markerLayout = null;
        }

        if (textChanged)
        {
            // Entry-granular invalidation cannot express a partial edit, so the cache is
            // replaced outright. See the class remarks.
            _runCache = new TextRunCache();

            // A composition is anchored at an offset into text that no longer exists, so it
            // cannot survive the edit that replaced that text. The view commits or abandons a
            // composition before editing; this is the backstop that keeps the anchor from
            // pointing past the end of the paragraph if it ever does not.
            _preedit = null;
        }

        return true;
    }

    /// <summary>
    /// Text an input method is composing here, or null for none.
    /// </summary>
    /// <remarks>
    /// The composition is spliced into the layout and is therefore visible to every geometry
    /// query, but it is not part of the paragraph: <see cref="BlockView.ContentSize"/> does not
    /// count it and no position this type hands out or accepts includes it. See
    /// <see cref="ToLayout"/>.
    /// </remarks>
    public Preedit? Preedit => _preedit;

    /// <summary>Sets or clears the composition being drawn in this paragraph.</summary>
    /// <param name="preedit">The composition, or null to clear it.</param>
    /// <returns>True if anything changed and the caller must redraw.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The composition is anchored outside the paragraph.
    /// </exception>
    public bool SetPreedit(Preedit? preedit)
    {
        if (preedit is not null && preedit.Offset > ContentSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preedit),
                preedit.Offset,
                $"A preedit must be anchored within [0, {ContentSize}] of its paragraph.");
        }

        if (preedit == _preedit)
        {
            return false;
        }

        _preedit = preedit;
        _layout = null;

        // Composing changes the text being shaped, so the same reasoning as an edit applies:
        // the cache cannot express a partial change and is replaced outright.
        _runCache = new TextRunCache();

        return true;
    }

    /// <summary>The length of what is laid out, which counts any composition.</summary>
    private int LayoutSize => ContentSize + (_preedit?.Length ?? 0);

    /// <summary>
    /// A paragraph-local position in the layout's coordinates.
    /// </summary>
    /// <remarks>
    /// The composition occupies positions the document does not have, so everything after its
    /// anchor is displaced by its length. The anchor itself maps to the <em>start</em> of the
    /// composition, which is what puts a caret at the anchor before the composed text rather
    /// than after it.
    /// </remarks>
    private int ToLayout(int localPosition) =>
        _preedit is { } preedit && localPosition > preedit.Offset
            ? localPosition + preedit.Length
            : localPosition;

    /// <summary>A layout position back in the paragraph's coordinates.</summary>
    /// <remarks>
    /// A position <em>inside</em> the composition has no document counterpart, so it collapses
    /// to the anchor. That is what stops a click on composing text from moving the model's
    /// caret into text the model does not have.
    /// </remarks>
    private int FromLayout(int layoutPosition)
    {
        if (_preedit is not { } preedit)
        {
            return layoutPosition;
        }

        return layoutPosition <= preedit.Offset
            ? layoutPosition
            : Math.Max(preedit.Offset, layoutPosition - preedit.Length);
    }

    /// <inheritdoc/>
    public override Size Measure(double availableWidth)
    {
        if (availableWidth <= 0 || double.IsNaN(availableWidth))
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableWidth),
                availableWidth,
                "A paragraph must be given a positive width to wrap to.");
        }

        if (_layout is not null && _layoutWidth.Equals(availableWidth))
        {
            return Size;
        }

        // The indent and the padding both eat into the width the text may wrap to, so a padded
        // or indented paragraph wraps where it visually runs out of room rather than where a
        // plain one would. A width smaller than that still has to leave somewhere for the text.
        var textWidth = Math.Max(1, availableWidth - Indent - _style.Padding.Right);

        _layout = new TextLayout(
            new InlineTextSource(_paragraph.Content, _styles, _embeds, _preedit),
            ParagraphProperties(),
            maxWidth: textWidth,
            textRunCache: _runCache);

        _layoutWidth = availableWidth;

        return Size;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The horizontal extent comes from <c>GetTextBounds</c>, which is right across a bidi
    /// boundary, and the vertical extent from the run itself, because the text bounds are as tall
    /// as the line. The run's top is the line's baseline less the run's own, which is how the
    /// formatter placed it — see <see cref="EmbedRun"/>.
    /// </remarks>
    public override Rect? GetImageRect(int localPosition)
    {
        // Deliberately kept although no test can distinguish it: the line search below happens to
        // reject an out-of-range offset, and Avalonia happens to tolerate a negative index in
        // GetTextBounds, but neither is a documented guarantee. Measured — removing this passes
        // the whole suite, which is why the mutation list carries a note rather than a mutant.
        if (localPosition < 0 || localPosition >= ContentSize)
        {
            return null;
        }

        var target = ToLayout(localPosition);
        var lineTop = 0.0;

        foreach (var line in Layout.TextLines)
        {
            if (target < line.FirstTextSourceIndex + line.Length)
            {
                foreach (var bounds in line.GetTextBounds(target, 1))
                {
                    foreach (var run in bounds.TextRunBounds)
                    {
                        if (run.TextRun is EmbedRun { Embed: ImageEmbed } embed)
                        {
                            return new Rect(
                                Origin.X + run.Rectangle.X,
                                Origin.Y + lineTop + line.Baseline - embed.Baseline,
                                embed.Size.Width,
                                embed.Size.Height);
                        }
                    }
                }

                return null;
            }

            lineTop += line.Height;
        }

        return null;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context, Point origin)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_style.Background is { } background)
        {
            // The block's content box: across the full column, not just as wide as the longest
            // line, and inside the outer spacing rather than over it. A fill that hugged the
            // glyphs would read as a highlight on the text, and one that covered SpaceBefore
            // would close the gap to the paragraph above and merge two code blocks into one.
            var width = double.IsNaN(_layoutWidth) ? Size.Width : _layoutWidth;
            var height = Size.Height - _style.SpaceBefore - _style.SpaceAfter;

            context.DrawRectangle(
                background,
                pen: null,
                new RoundedRect(
                    new Rect(origin.X, origin.Y + _style.SpaceBefore, width, height),
                    BackgroundCornerRadius));
        }

        if (_style.Bar is { } bar)
        {
            // Down the whole block including its spacing, so a run of quoted paragraphs reads as
            // one quotation rather than a column of dashes.
            context.FillRectangle(
                bar, new Rect(origin.X, origin.Y, _style.BarWidth, Size.Height));
        }

        Layout.Draw(context, origin + Origin);

        if (MarkerLayout is { } marker)
        {
            // Right-aligned into the gutter the depth opened up, and on the paragraph's own
            // first baseline so a bullet lines up with the text it belongs to rather than with
            // the top of the block.
            var baseline = Layout.TextLines[0].Baseline - marker.TextLines[0].Baseline;

            marker.Draw(
                context,
                origin + new Point(Indent - marker.Width - MarkerGap, Origin.Y + baseline));
        }
    }

    /// <summary>The gap between a list marker and the text it introduces, in device pixels.</summary>
    public const double MarkerGap = 6;

    /// <summary>What is drawn in front of the paragraph, or null when nothing is.</summary>
    public string? MarkerText => _marker is { } marker
        ? marker.Kind == ListKind.Ordered ? $"{marker.Ordinal}." : "\u2022"
        : null;

    /// <summary>The laid-out bullet or number, or null when there is none to draw.</summary>
    private TextLayout? MarkerLayout
    {
        get
        {
            if (MarkerText is not { } text)
            {
                return null;
            }

            return _markerLayout ??= new TextLayout(
                text,
                _styles.Default.Typeface,
                _styles.Default.FontRenderingEmSize,
                _styles.Default.ForegroundBrush);
        }
    }

    /// <inheritdoc/>
    public override int HitTest(Point local)
    {
        var hit = Layout.HitTestPoint(local - Origin);

        // TextPosition already carries the trailing adjustment: the trailing half of a
        // character reports the position after it, and it does so in whole clusters, so a
        // surrogate pair or a combining sequence is never split by a click.
        return FromLayout(Math.Clamp(hit.TextPosition, 0, LayoutSize));
    }

    /// <inheritdoc/>
    public override Rect GetCaretRect(int localPosition)
    {
        CheckPosition(localPosition);

        return CaretRectInLayout(ToLayout(localPosition));
    }

    /// <summary>The caret rectangle for a composition's own cursor.</summary>
    /// <remarks>
    /// Separate from <see cref="GetCaretRect"/> because a composition cursor sits at a layout
    /// position the document has no counterpart for, which is exactly what that method refuses
    /// to accept.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Nothing is being composed here.</exception>
    public Rect GetPreeditCaretRect()
    {
        if (_preedit is not { } preedit)
        {
            throw new InvalidOperationException("Nothing is being composed in this paragraph.");
        }

        return CaretRectInLayout(Math.Clamp(preedit.CaretPosition, 0, LayoutSize));
    }

    private Rect CaretRectInLayout(int layoutPosition)
    {
        var rect = Layout.HitTestTextPosition(layoutPosition);

        return new Rect(Origin.X + rect.X, Origin.Y + rect.Y, 0, rect.Height);
    }

    /// <inheritdoc/>
    /// <remarks>A paragraph is the paragraph, wherever inside it the position falls.</remarks>
    public override TextAt? ParagraphAt(int localPosition) => new(this, 0, default);

    /// <inheritdoc/>
    public override IReadOnlyList<Rect> GetSelectionRects(int from, int to)
    {
        CheckPosition(from);
        CheckPosition(to);
        var start = ToLayout(Math.Min(from, to));
        var length = ToLayout(Math.Max(from, to)) - start;

        if (length == 0)
        {
            return [];
        }

        var rects = new List<Rect>();
        var lineTop = 0.0;

        foreach (var line in Layout.TextLines)
        {
            // A line's range is clipped against the selection rather than tested for
            // containment, because a selection routinely covers part of a line at each end.
            // Avalonia clamps an overrunning length itself — there is a test pinning that — so
            // the upper clip is defensive; the lower one is not.
            var lineStart = Math.Max(start, line.FirstTextSourceIndex);
            var lineEnd = Math.Min(start + length, line.FirstTextSourceIndex + line.Length);

            if (lineEnd > lineStart)
            {
                foreach (var bounds in line.GetTextBounds(lineStart, lineEnd - lineStart))
                {
                    // One TextBounds per direction run, so a selection crossing a bidi boundary
                    // arrives here as several disjoint rectangles. Increment 0 measured this.
                    //
                    // The rectangle is measured from the top of its own line, not from the top
                    // of the layout, so the line's offset has to be added or every line of a
                    // wrapped selection lands on the first one. Measured before this was here:
                    // a ten-line selection produced ten rectangles all at y=0.
                    rects.Add(bounds.Rectangle.Translate(Origin + new Vector(0, lineTop)));
                }
            }

            lineTop += line.Height;
        }

        return rects;
    }

    /// <summary>
    /// The next caret position after <paramref name="localPosition"/>, never landing inside a
    /// grapheme cluster.
    /// </summary>
    /// <param name="localPosition">The position to move from.</param>
    public int NextCaretPosition(int localPosition) =>
        Move(localPosition, forward: true, static (line, hit) => line.GetNextCaretCharacterHit(hit));

    /// <summary>
    /// The previous caret position before <paramref name="localPosition"/>, never landing
    /// inside a grapheme cluster.
    /// </summary>
    /// <param name="localPosition">The position to move from.</param>
    public int PreviousCaretPosition(int localPosition) =>
        Move(localPosition, forward: false, static (line, hit) => line.GetPreviousCaretCharacterHit(hit));

    /// <summary>
    /// Where a Backspace from <paramref name="localPosition"/> should delete back to.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="PreviousCaretPosition"/>: Backspace peels one combining mark
    /// off a cluster where the arrow key steps over the whole cluster. Increment 0 measured the
    /// difference and the user chose to keep the component-peel behaviour.
    /// </remarks>
    /// <param name="localPosition">The caret position to delete back from.</param>
    public int BackspacePosition(int localPosition) =>
        Move(localPosition, forward: false, static (line, hit) => line.GetBackspaceCaretCharacterHit(hit));

    /// <summary>The number of visual lines the paragraph wrapped onto.</summary>
    public int LineCount => Layout.TextLines.Count;

    /// <summary>The index of the visual line a position sits on.</summary>
    /// <remarks>
    /// A wrap boundary belongs to two lines, so the caller says which way it is travelling for
    /// the same reason <see cref="Move"/> does.
    /// </remarks>
    /// <param name="localPosition">The position to locate.</param>
    /// <param name="forward">Whether the caret is moving forward.</param>
    public int LineIndexAt(int localPosition, bool forward)
    {
        CheckPosition(localPosition);

        var layoutPosition = ToLayout(localPosition);
        var lines = Layout.TextLines;

        for (var i = 0; i < lines.Count; i++)
        {
            var end = lines[i].FirstTextSourceIndex + lines[i].Length;

            var owns = forward
                ? layoutPosition < end
                : layoutPosition <= end && layoutPosition > lines[i].FirstTextSourceIndex;

            if (owns)
            {
                return i;
            }
        }

        return forward ? lines.Count - 1 : 0;
    }

    /// <summary>The first position on a visual line.</summary>
    /// <param name="lineIndex">The line.</param>
    public int LineStart(int lineIndex) =>
        FromLayout(Math.Clamp(Line(lineIndex).FirstTextSourceIndex, 0, LayoutSize));

    /// <summary>The last position on a visual line.</summary>
    /// <remarks>
    /// Clamped to <see cref="BlockView.ContentSize"/>, because the last line's text source
    /// length counts the paragraph terminator, which is not a position the caret can occupy.
    /// Trailing whitespace that wrapped is excluded for the same reason a caret at End should
    /// not sit past the visible edge of the line.
    /// </remarks>
    /// <param name="lineIndex">The line.</param>
    public int LineEnd(int lineIndex)
    {
        var line = Line(lineIndex);
        var end = line.FirstTextSourceIndex + line.Length - line.NewLineLength - line.TrailingWhitespaceLength;

        return Math.Max(LineStart(lineIndex), FromLayout(Math.Clamp(end, 0, LayoutSize)));
    }

    /// <summary>The position on a visual line nearest a horizontal offset.</summary>
    /// <remarks>
    /// This is what carries a caret's goal column across a vertical move. The offset is in this
    /// block's coordinates, so it includes <see cref="Indent"/>.
    /// </remarks>
    /// <param name="lineIndex">The line to land on.</param>
    /// <param name="x">The horizontal offset to land nearest.</param>
    public int PositionAtLineX(int lineIndex, double x)
    {
        var hit = Line(lineIndex).GetCharacterHitFromDistance(x - Origin.X);

        return FromLayout(Math.Clamp(hit.FirstCharacterIndex + hit.TrailingLength, 0, LayoutSize));
    }

    private TextLine Line(int lineIndex)
    {
        var lines = Layout.TextLines;

        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lineIndex), lineIndex, $"The paragraph has {lines.Count} lines.");
        }

        return lines[lineIndex];
    }

    private int Move(int localPosition, bool forward, Func<TextLine, CharacterHit, CharacterHit> step)
    {
        CheckPosition(localPosition);

        var line = LineAt(localPosition, forward);
        var hit = step(line, new CharacterHit(ToLayout(localPosition)));

        return FromLayout(Math.Clamp(hit.FirstCharacterIndex + hit.TrailingLength, 0, LayoutSize));
    }

    /// <summary>
    /// The line to ask about a move from <paramref name="localPosition"/>.
    /// </summary>
    /// <remarks>
    /// A wrap boundary is one position belonging to two lines, and <see cref="TextLine"/>'s
    /// caret methods refuse to leave the line they are called on. So the line is chosen by the
    /// direction of travel: moving left from a wrap boundary asks the line that <em>ends</em>
    /// there, which is the only one that can answer. Getting this wrong does not throw — it
    /// silently pins the caret at the start of every wrapped line.
    /// </remarks>
    private TextLine LineAt(int localPosition, bool forward) =>
        Layout.TextLines[LineIndexAt(localPosition, forward)];

    /// <summary>
    /// The paragraph-level properties handed to the formatter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base direction is fixed left-to-right, which is a deliberate deferral rather than an
    /// oversight. Bidirectional text <em>within</em> a line is handled by the formatter and was
    /// measured in Increment 0; what is missing is a paragraph whose base direction is
    /// right-to-left, so that alignment resolves to the right edge and neutrals at the ends of
    /// the line settle the other way. That is a document-model question — the model has no
    /// direction attribute on a paragraph and inferring one from content is a decision with
    /// visible consequences — so it waits for the model to carry the answer.
    /// </para>
    /// <para>
    /// <c>indent</c> is zero here on purpose: Avalonia's paragraph indent applies to the first
    /// line only, whereas an indent level insets the whole block. See <see cref="Indent"/>.
    /// </para>
    /// <para>
    /// The line height is resolved against the block's own already-scaled default size rather
    /// than being carried as pixels, so a heading and a body paragraph get the leading each
    /// wants from one number in the style. Zero leaves the spacing to the font.
    /// </para>
    /// <para>
    /// A forced line height is <em>absolute</em> in Avalonia, not a minimum: a line holding a
    /// drawable run taller than it keeps the forced height, and the drawable spills out of the
    /// paragraph and over whatever is drawn below. Measured on 12.1.1 — a 40pt image on a 19.2pt
    /// line reported <c>Height</c> 19.2 and <c>Baseline</c> 40, which cannot describe a real box.
    /// So a paragraph whose tallest embed does not fit gives the leading up and lets the font
    /// decide, which grows only the lines that need it rather than every line in the paragraph.
    /// </para>
    /// </remarks>
    private TextParagraphProperties ParagraphProperties()
    {
        var scaled = _style.LineHeightScale > 0
            ? _style.LineHeightScale * _styles.Default.FontRenderingEmSize
            : 0;

        return new GenericTextParagraphProperties(
            FlowDirection.LeftToRight,
            Alignment(_paragraph.Align),
            firstLineInParagraph: true,
            alwaysCollapsible: false,
            _styles.Default,
            TextWrapping.Wrap,
            lineHeight: scaled >= TallestEmbed() ? scaled : 0,
            indent: 0,
            letterSpacing: 0);
    }

    /// <summary>The height of the tallest embed in the paragraph, or zero if it has none.</summary>
    /// <remarks>
    /// Measured through the same path <see cref="EmbedRun"/> takes, including its fallback for an
    /// embed that does not say how big it is, so the answer cannot disagree with the run that is
    /// actually laid out. The common case — a paragraph with no embeds — allocates nothing.
    /// </remarks>
    private double TallestEmbed()
    {
        var tallest = 0.0;

        foreach (var embed in _paragraph.Content.Embeds)
        {
            tallest = Math.Max(
                tallest, new EmbedRun(embed, _styles.Default, _embeds).Size.Height);
        }

        return tallest;
    }

    private static TextAlignment Alignment(TextAlign align) => align switch
    {
        TextAlign.Left => TextAlignment.Left,
        TextAlign.Center => TextAlignment.Center,
        TextAlign.Right => TextAlignment.Right,
        TextAlign.Justify => TextAlignment.Justify,
        _ => TextAlignment.Start,
    };

    private void CheckPosition(int localPosition)
    {
        if (localPosition < 0 || localPosition > ContentSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localPosition),
                localPosition,
                $"Position must be within [0, {ContentSize}] for this paragraph.");
        }
    }
    /// <summary>
    /// An <see cref="ITextStyleResolver"/> with a paragraph kind's presentation folded in.
    /// </summary>
    /// <remarks>
    /// A decorator rather than a parameter on the resolver interface, because the kind is fixed
    /// for the whole paragraph: the text source asks per run, and the run has no idea what block
    /// it is in. Wrapping means the source stays ignorant and hosts keep the simpler interface.
    /// </remarks>
    private sealed class KindStyles : ITextStyleResolver
    {
        private readonly Dictionary<MarkSet, TextRunProperties> _cache = [];
        private readonly ITextStyleResolver _inner;
        private readonly ParagraphStyle _style;

        private KindStyles(ITextStyleResolver inner, ParagraphStyle style)
        {
            _inner = inner;
            _style = style;
            Default = Apply(MarkSet.Empty, inner.Default);
        }

        public TextRunProperties Default { get; }

        /// <summary>The resolver to use for a kind, which is the original one where it changes nothing.</summary>
        public static ITextStyleResolver Wrap(ITextStyleResolver inner, ParagraphStyle style) =>
            style.FontScale == 1 && style.Weight is null && style.Family is null
                ? inner
                : new KindStyles(inner, style);

        public TextRunProperties Resolve(MarkSet marks)
        {
            if (_cache.TryGetValue(marks, out var cached))
            {
                return cached;
            }

            var built = Apply(marks, _inner.Resolve(marks));

            _cache[marks] = built;

            return built;
        }

        private TextRunProperties Apply(MarkSet marks, TextRunProperties properties)
        {
            var typeface = properties.Typeface;

            // The marks win where they are explicit. A word deliberately set to a family or to
            // bold keeps that inside a heading or a code block; the kind only supplies what was
            // not asked for.
            var family = marks.FontFamily is null && _style.Family is { } kindFamily
                ? kindFamily
                : typeface.FontFamily;

            var weight = !marks.Has(TextStyle.Bold) && _style.Weight is { } kindWeight
                ? kindWeight
                : typeface.Weight;

            return new GenericTextRunProperties(
                new Typeface(family, typeface.Style, weight, typeface.Stretch),
                fontRenderingEmSize: properties.FontRenderingEmSize * _style.FontScale,
                textDecorations: properties.TextDecorations,
                foregroundBrush: properties.ForegroundBrush,
                backgroundBrush: properties.BackgroundBrush,
                baselineAlignment: properties.BaselineAlignment,
                cultureInfo: properties.CultureInfo);
        }
    }
}