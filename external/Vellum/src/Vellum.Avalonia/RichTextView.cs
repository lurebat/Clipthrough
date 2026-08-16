using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Vellum.Avalonia;

/// <summary>
/// The control that presents a document and draws the caret and the selection, per
/// architecture 4.6.
/// </summary>
/// <remarks>
/// <para>
/// The division of labour: blocks own layout and answer for geometry in their own coordinates,
/// and this control owns the block list, the caret, the selection highlight and everything
/// that has to be true across blocks. The block list and the reconciliation that keeps it
/// cheap live in the Blocks part of this class.
/// </para>
/// <para>
/// The caret and the highlight are drawn here rather than by the block precisely because a
/// selection spans blocks and the caret is a single concern with one blink timer. A block that
/// drew its own caret would need to know whether it held the focused end of the selection,
/// which is knowledge it has no business having.
/// </para>
/// </remarks>
public partial class RichTextView : DocumentPresenter
{
    /// <summary>Defines the <see cref="State"/> property.</summary>
    public static readonly DirectProperty<RichTextView, EditorState> StateProperty =
        AvaloniaProperty.RegisterDirect<RichTextView, EditorState>(
            nameof(State), o => o.State, (o, v) => o.State = v);

    /// <summary>Defines the <see cref="SelectionBrush"/> property.</summary>
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<RichTextView, IBrush?>(
            nameof(SelectionBrush), new SolidColorBrush(Color.FromArgb(80, 0, 120, 215)));

    /// <summary>Defines the <see cref="CaretBrush"/> property.</summary>
    /// <remarks>
    /// Null means "whatever the text is painted in", which is the default because a fixed colour
    /// is wrong under half the themes there are. Measured: with a hard-coded black default the
    /// caret was invisible in the dark-themed demo, which reads as the control ignoring the
    /// keyboard entirely. Set it to paint the caret in something other than the text colour.
    /// </remarks>
    public static readonly StyledProperty<IBrush?> CaretBrushProperty =
        AvaloniaProperty.Register<RichTextView, IBrush?>(nameof(CaretBrush));

    /// <summary>The interval at which the caret changes state.</summary>
    public static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _blink;
    private EditorState _state = EditorState.Create(DocumentNode.Empty);
    private bool _caretVisible;
    private int _caretBlock;

    /// <summary>Creates the view.</summary>
    public RichTextView()
    {
        Focusable = true;

        // Set per pointer move from IsOverContent, not once here: an I-beam pinned to the whole
        // control claims the empty space below the document is text.
        Cursor = ArrowCursor;

        ContextFlyout = BuildContextFlyout();

        // A drop is a transfer from another application, exactly like a paste, so it is handled
        // here rather than left to the host to wire up.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        _inputMethod = new RichTextInputMethodClient(this);

        // How a control offers itself to the platform input method: the request bubbles from
        // the focused element, and answering it is what makes composition arrive here at all.
        AddHandler(
            global::Avalonia.Input.InputElement.TextInputMethodClientRequestedEvent,
            (_, e) => e.Client = _inputMethod);

        _blink = new DispatcherTimer { Interval = BlinkInterval };
        _blink.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };
    }

    /// <summary>The document and selection being presented.</summary>
    public EditorState State
    {
        get => _state;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (ReferenceEquals(_state, value))
            {
                return;
            }

            SetState(value, derived: false);
        }
    }

    /// <summary>Installs a new state, and decides what becomes of the history.</summary>
    /// <param name="value">The state to install.</param>
    /// <param name="derived">
    /// Whether the state came from this control editing the one before it, as opposed to a host
    /// loading a different document.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Loading a different document discards the history</b>, because the history describes
    /// edits to a document that is no longer here. Undoing it against the new one at best restores
    /// text from somewhere else and at worst throws, since the positions it recorded need not
    /// exist — measured, before this existed: typing into a four-character clip, loading a
    /// one-character clip and pressing Ctrl+Z threw <c>Range [3, 4) is not inside the document</c>.
    /// A clipboard history or a file list hits that by arrowing down and pressing a key.
    /// </para>
    /// <para>
    /// Only a change of <em>document</em> counts. Assigning a state that carries the same document
    /// with a different selection is how selection is set from outside — <see cref="SelectAll"/>
    /// does it — and must not cost the user their undo stack.
    /// </para>
    /// <para>
    /// The limit survives, since it is the host's configuration rather than the document's
    /// history. A host that genuinely wants the old stack can put it back through
    /// <see cref="History"/> afterwards.
    /// </para>
    /// </remarks>
    private void SetState(EditorState value, bool derived)
    {
        // The view reconciles before the change is announced, not after. A consumer handling
        // StateProperty asks the view questions -- which block the caret is in, what the caret
        // block's style is -- and those are answered from state that OnStateChanged rebuilds.
        // Raising first hands every listener the previous document's answers.
        var previous = _state;
        _state = value;

        if (!derived && !ReferenceEquals(previous.Doc, value.Doc))
        {
            History = History.With(_history.Policy);
        }

        OnStateChanged();
        RaisePropertyChanged(StateProperty, previous, value);
    }

    /// <summary>The fill behind selected text.</summary>
    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>The caret's colour.</summary>
    public IBrush? CaretBrush
    {
        get => GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    /// <inheritdoc/>
    protected internal override DocumentNode PresentedDocument => _state.Doc;

    /// <inheritdoc/>
    protected internal override bool SupportsTextInput => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Selecting is not an edit, so the whole replacement is one transaction and one undo takes
    /// it back. Empty text is a deletion rather than a paste of nothing, which
    /// <see cref="PasteText"/> would treat as no change at all.
    /// </remarks>
    protected internal override void SetTextFromAutomation(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        SelectAll();

        if (text.Length == 0)
        {
            DeleteSelection();
        }
        else
        {
            PasteText(text);
        }
    }

    /// <summary>The index of the block the caret is in.</summary>
    public int CaretBlockIndex => _caretBlock;

    /// <summary>
    /// The paragraph the caret is in, where it starts and where it is drawn, or null.
    /// </summary>
    /// <remarks>
    /// Recomputed rather than cached because the caret moves far more often than the layout
    /// changes and a stale copy would edit the wrong paragraph. It is a search through one
    /// block, not the document.
    /// </remarks>
    private TextAt? CaretText =>
        _views.Count == 0
            ? null
            : _views[_caretBlock].ParagraphAt(
                Math.Clamp(
                    _state.Selection.Head - _slots[_caretBlock].Start,
                    0,
                    _views[_caretBlock].ContentSize));

    /// <summary>
    /// The view of the paragraph the caret is in, or null before the control has been measured.
    /// </summary>
    /// <remarks>
    /// Null when the caret is somewhere no text lives — a rule, an image — since
    /// <see cref="Selection.Near"/> puts a text caret wherever one can go.
    /// </remarks>
    /// <remarks>
    /// Not the same thing as the block at <see cref="CaretBlockIndex"/>: a table is one block
    /// holding many paragraphs, and every editing command wants the paragraph.
    /// </remarks>
    public ParagraphView? Block => CaretText?.View;

    /// <summary>
    /// The document position at which the caret's paragraph starts.
    /// </summary>
    /// <remarks>
    /// The one place a paragraph-local position becomes a document position. Paired with
    /// <see cref="Block"/> — the two are always read together and must come from the same
    /// paragraph, which is why both go through <see cref="CaretText"/>.
    /// </remarks>
    public int BlockStart =>
        _slots.IsEmpty ? 0 : _slots[_caretBlock].Start + (CaretText?.Start ?? 0);

    /// <summary>Where the caret's paragraph is drawn, in this control's coordinates.</summary>
    private Point BlockOffset
    {
        get
        {
            var origin = BlockOrigin(_caretBlock);
            var inner = CaretText?.Origin ?? default;

            return new Point(origin.X + inner.X, origin.Y + inner.Y);
        }
    }

    /// <summary>Whether the caret is currently painted, for tests and for the blink timer.</summary>
    public bool IsCaretVisible => _caretVisible;

    /// <inheritdoc/>
    /// <remarks>
    /// Order matters and was settled by looking at the result. Found-but-not-current matches go
    /// under the selection, because a match the user has also selected should read as selected.
    /// The current match goes over it, because it is always selected and would otherwise be a
    /// muddy mixture of the two. Text is above all of them, the image handles above the text
    /// they overlap, and the caret above everything.
    /// </remarks>
    private protected override void RenderContent(DrawingContext context)
    {
        RenderFindHighlights(context);
        RenderSelection(context);
        RenderCurrentFindHighlight(context);
        RenderBlocks(context);
        RenderImageHandles(context);
        RenderTouchHandles(context);
        RenderCaret(context);
    }
    /// <summary>Restarts the blink so the caret is solid immediately after it moves.</summary>
    /// <remarks>
    /// Without this a caret moved just before the timer ticks vanishes on arrival, which reads
    /// as a dropped keystroke.
    /// </remarks>
    public void ResetCaretBlink()
    {
        _caretVisible = IsFocused;
        _blink.Stop();

        if (IsFocused)
        {
            _blink.Start();
        }

        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnGotFocus(global::Avalonia.Input.FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        ResetCaretBlink();
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(global::Avalonia.Input.FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        // Touch chrome is an affordance for adjusting a selection the user is looking at. A view
        // that is not focused has no such selection on offer.
        HideTouchHandles();
        ResetCaretBlink();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // A dispatcher timer holds the control alive through its Tick handler, so a view that
        // leaves the tree without stopping it keeps repainting nothing forever.
        _blink.Stop();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A derived resolver is only as current as the properties it was derived from, so a
        // font change has to invalidate it. An explicitly set one is the caller's to manage;
        // the base class owns that decision, and this only adds the brushes it knows nothing of.
        if (change.Property == SelectionBrushProperty || change.Property == CaretBrushProperty)
        {
            InvalidateVisual();
        }

        if (change.Property == UndoLimitProperty)
        {
            OnUndoLimitChanged(change.GetNewValue<int>());
        }

        // Read-only changes what Undo and Redo will do, so a bound button has to hear about it.
        // The caret stays: a read-only TextBox shows one too, and it is what tells a reader
        // where a selection they are about to extend actually starts.
        if (change.Property == IsReadOnlyProperty)
        {
            RaiseHistoryFlags((
                !change.GetOldValue<bool>() && _history.CanUndo,
                !change.GetOldValue<bool>() && _history.CanRedo));
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The caret's block, so that <see cref="OnReconciled"/> can tell whether the caret has
    /// moved to a different paragraph while a composition was in progress.
    /// </remarks>
    private protected override BlockView? Anchor =>
        _caretBlock < _views.Count ? _views[_caretBlock] : null;

    /// <inheritdoc/>
    private protected override void OnReconciled(BlockView? before)
    {
        _caretBlock = Math.Min(BlockIndexOf(_state.Selection.Head), Math.Max(0, _views.Count - 1));

        // A composition belongs to the block the caret was in. If the caret has left, the
        // composition has to go with it: leaving it behind would draw composed text in a
        // paragraph the input method is no longer talking about, and it would never be
        // committed there.
        if (before is ParagraphView left
            && !ReferenceEquals(left, _views.Count == 0 ? null : _views[_caretBlock]))
        {
            left.SetPreedit(null);
        }
    }

    /// <inheritdoc/>
    private protected override void OnLayoutDropped() => _caretBlock = 0;

    /// <inheritdoc/>
    /// <summary>
    /// Scrolls until the caret is inside the viewport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wanted whenever the selection moves, which is the only time a caret can leave the screen
    /// on its own. Typing at the bottom of a screenful and watching the line you are writing
    /// disappear is the defect this exists to prevent.
    /// </para>
    /// <para>
    /// Called from <c>Realize</c> rather than straight from the state change, because where the
    /// caret <em>is</em> depends on the heights of every block above it, and those are not
    /// settled until the window has been laid out.
    /// </para>
    /// <para>
    /// The caret's block may be nowhere near the realized window — Ctrl+End is exactly that — so
    /// it is laid out on demand before it is asked where its caret is.
    /// </para>
    /// </remarks>
    /// <returns>Whether the view moved.</returns>
    private protected override bool ScrollAnchorIntoView()
    {
        if (!IsVirtualizing || _views.Count == 0)
        {
            return false;
        }

        EnsureMeasured(_caretBlock);

        // A block with no caret in it — a rule, an image — has no caret rectangle to aim at,
        // so the whole block is brought into view instead.
        var caret = Block is null
            ? new Rect(0, BlockOrigin(_caretBlock).Y, 0, _views[_caretBlock].Size.Height)
            : CaretRect();

        // Measured against the constraint, not against the arranged viewport: a document can
        // arrive with its caret already at the end — a restored one does — and the first measure
        // runs before any arrange, so the arranged viewport is still nothing. The constraint is
        // known by then, and is the same height.
        var delta = 0d;

        if (caret.Bottom > ViewportHeight)
        {
            delta = caret.Bottom - ViewportHeight;
        }

        // Checked after the downward correction and allowed to override it: a caret taller than
        // the viewport cannot be shown whole, and showing its top is more use than its bottom.
        if (caret.Top - delta < 0)
        {
            delta = caret.Top;
        }

        // No dead zone around the delta. A caret already on screen produces exactly zero, and
        // the comparison below is what reports whether anything moved; a sub-pixel threshold on
        // top of that was tried and could not be shown to prevent anything — swept over 200
        // scroll positions, no repeated layout ever moved the view by a fraction of a pixel.
        var before = _offset;

        ApplyOffset(new Vector(_offset.X, _offset.Y + delta), relayout: false);

        return _offset != before;
    }

    private void OnStateChanged()
    {
        // Consumed by Realize, not acted on here: where the caret is depends on the heights of
        // every block above it, and those are not settled until the window has been laid out.
        _pendingAnchor = true;

        // Before the layout work, so a research that changes nothing does not also cost a frame.
        Research();

        if (_views.Count > 0)
        {
            // Realised eagerly rather than waiting for the layout pass: see Realize.
            Realize();
        }

        InvalidateMeasure();
        ResetCaretBlink();
        NotifyInputMethod();
    }

    /// <summary>
    /// The rectangle the selection occupies on screen, in view coordinates, or null when nothing
    /// is selected or no part of the selection has been realized.
    /// </summary>
    /// <remarks>
    /// The union of the rectangles the selection paints, and so bounded by the realized window
    /// rather than by the document: a selection running off the bottom of the viewport reports
    /// only the part that is on screen. That is what a caller putting something beside the
    /// selection actually wants, since there is nowhere off screen to put it.
    /// </remarks>
    public Rect? SelectionBounds()
    {
        Rect? bounds = null;

        EnumerateSelection(rect => bounds = bounds is { } seen ? seen.Union(rect) : rect);

        return bounds;
    }

    private void RenderSelection(DrawingContext context)
    {
        if (SelectionBrush is not { } brush)
        {
            return;
        }

        EnumerateSelection(rect => context.FillRectangle(brush, rect));
    }

    /// <summary>Walks the rectangles the current selection covers, in view coordinates.</summary>
    /// <remarks>
    /// Shared by painting and by <see cref="SelectionBounds"/> so that the two can never disagree
    /// about where the selection is — a toolbar floating beside a highlight that is somewhere else
    /// is worse than no toolbar.
    /// </remarks>
    private void EnumerateSelection(Action<Rect> onRect)
    {
        if (_state.Selection.IsEmpty)
        {
            return;
        }

        if (_state.Selection is CellSelection rectangle)
        {
            EnumerateCells(rectangle, onRect);

            return;
        }

        EnumerateRange(_state.Selection.From, _state.Selection.To, onRect);
    }

    /// <summary>Walks the boxes of the cells a rectangular selection covers.</summary>
    /// <remarks>
    /// A rectangle is not a range: selecting the left column of a two-column table skips the
    /// right one in every row, so walking <c>From</c> to <c>To</c> would highlight cells the
    /// reader did not select. The cells are asked for by position instead.
    /// </remarks>
    private void EnumerateCells(CellSelection selection, Action<Rect> onRect)
    {
        var cells = selection.Cells(_state.Doc);

        if (cells.IsEmpty)
        {
            return;
        }

        var wanted = cells.ToHashSet();

        for (var i = _first; i <= _last; i++)
        {
            if (_views[i] is not TableBlockView table)
            {
                continue;
            }

            var origin = BlockOrigin(i);

            foreach (var rect in table.GetCellRects(wanted, _slots[i].Start))
            {
                onRect(rect.Translate(origin));
            }
        }
    }

    /// <summary>Walks the rectangles a document range covers, in view coordinates.</summary>
    /// <remarks>
    /// Shared by the selection, by <see cref="SelectionBounds"/> and by the find highlight, so
    /// that none of them can disagree about where a range is — a toolbar floating beside a
    /// highlight that is somewhere else is worse than no toolbar.
    /// </remarks>
    private void EnumerateRange(int from, int to, Action<Rect> onRect)
    {
        for (var i = _first; i <= _last; i++)
        {
            var slot = _slots[i];
            var origin = BlockOrigin(i);

            if (slot.Node.IsLeaf)
            {
                // A leaf has no inside, so there is no part of it to select: either the
                // selection reaches across it and it is covered whole, or it is not touched.
                // The host answers this because only the host can see that the selection
                // reaches past the block on both sides.
                if (from <= slot.Start && to >= slot.Start + slot.Node.NodeSize)
                {
                    onRect(new Rect(origin, _views[i].Size));
                }

                continue;
            }

            // Blocks the range does not reach into at all. The comparisons are strict at
            // both ends so that a range merely touching a boundary is skipped here rather
            // than asked for a zero-length range; GetSelectionRects answers that with nothing,
            // so this is a short-circuit, not the thing that keeps an empty sliver off screen.
            if (to <= slot.Start || from >= slot.End)
            {
                continue;
            }

            var view = _views[i];

            foreach (var rect in view.GetSelectionRects(
                Math.Max(0, from - slot.Start),
                Math.Min(view.ContentSize, to - slot.Start)))
            {
                onRect(rect.Translate(origin));
            }
        }
    }

    private void RenderCaret(DrawingContext context)
    {
        if (!_caretVisible || _views.Count == 0)
        {
            return;
        }

        // A non-empty selection hides the caret, except while composing: then the caret belongs
        // to the input method and sits inside text the selection knows nothing about.
        if (Block?.Preedit is null && !_state.Selection.IsEmpty)
        {
            return;
        }

        // Falls back to the colour the text itself is painted in, so the caret follows the
        // theme rather than a colour chosen when the control was written. See CaretBrushProperty.
        if ((CaretBrush ?? TextStyles.Resolve(MarkSet.Empty).ForegroundBrush) is not { } brush)
        {
            return;
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

        context.FillRectangle(brush, SnapCaret(CaretRect(), scale));
    }

    /// <summary>
    /// Snaps a caret rectangle to the device pixel grid and gives it a width of exactly one
    /// device pixel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Architecture 4.6 says to follow <c>TextPresenter</c> with
    /// <c>PixelRect.FromRect(rect, scale).ToRect(scale)</c>. That does not do what is wanted
    /// here, and it was measured: <c>PixelRect.FromRect</c> is a <em>covering</em> conversion,
    /// flooring the left edge and ceiling the right, so a one-wide rectangle at a fractional x
    /// comes back two pixels wide — precisely the blurred double-width caret the snapping was
    /// meant to prevent. <c>FromRect(new Rect(10.7, 4.2, 1, 18.6), 1)</c> returns
    /// <c>10, 4, 2, 19</c>.
    /// </para>
    /// <para>
    /// So the origin is rounded to the grid and the width is set to one device pixel outright.
    /// The height is snapped by rounding both edges, which keeps the caret the same height as
    /// the line rather than letting rounding shave it.
    /// </para>
    /// </remarks>
    /// <param name="caret">The caret rectangle in device-independent pixels.</param>
    /// <param name="scale">The render scaling of the surface being drawn on.</param>
    public static Rect SnapCaret(Rect caret, double scale)
    {
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale), scale, "Render scaling must be a positive, finite number.");
        }

        var x = Math.Round(caret.X * scale) / scale;
        var top = Math.Round(caret.Y * scale) / scale;
        var bottom = Math.Round((caret.Y + caret.Height) * scale) / scale;

        return new Rect(x, top, CaretWidth / scale, bottom - top);
    }
}
