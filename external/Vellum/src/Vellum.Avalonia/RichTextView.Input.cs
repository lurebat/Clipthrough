using Avalonia;
using Avalonia.Input;

namespace Vellum.Avalonia;

/// <summary>
/// Pointer and keyboard selection.
/// </summary>
/// <remarks>
/// <para>
/// Everything here goes through <see cref="MoveTo"/>, which is the only place a caret movement
/// turns into a new selection. Input handlers decide <em>where</em>; they never decide how a
/// selection is built, so extend-with-Shift and drag-with-the-mouse cannot drift apart.
/// </para>
/// <para>
/// No handler here edits the document. Typing arrives in the next piece of work; selection has
/// to be right first, because every edit is expressed relative to it.
/// </para>
/// </remarks>
public partial class RichTextView
{
    /// <summary>Defines the <see cref="IsDragging"/> property.</summary>
    public static readonly DirectProperty<RichTextView, bool> IsDraggingProperty =
        AvaloniaProperty.RegisterDirect<RichTextView, bool>(nameof(IsDragging), o => o.IsDragging);

    private bool _dragging;
    private double? _goalX;
    private int _cellOrigin;

    /// <summary>
    /// The horizontal offset a vertical move is trying to keep, if one is in progress.
    /// </summary>
    /// <remarks>
    /// Exposed for tests. A run of Up/Down keeps the column the caret started from rather than
    /// the column it reached, so passing through a short line does not permanently pull the
    /// caret left. Any other movement clears it.
    /// </remarks>
    public double? GoalX => _goalX;

    /// <summary>Whether a pointer drag is currently extending the selection.</summary>
    /// <remarks>
    /// A property rather than a field because anything showing an affordance for the selection
    /// has to wait for the drag to finish: a toolbar that appears on the first pixel of a drag
    /// then chases the pointer across the paragraph is unusable, and worse, it lands under the
    /// pointer and swallows the release.
    /// </remarks>
    public bool IsDragging
    {
        get => _dragging;
        private set => SetAndRaise(IsDraggingProperty, ref _dragging, value);
    }

    /// <summary>Places the caret, or extends the selection to reach it.</summary>
    /// <remarks>
    /// <para>
    /// The position is snapped to the nearest position text actually lives at before a selection
    /// is built from it. Handing a raw position to <see cref="TextSelection.Create"/> instead
    /// looks equivalent and is not: given a head that is not in text it repairs by calling
    /// <c>Selection.Near</c>, which returns a <em>cursor</em> and so silently discards the
    /// anchor. Measured — extending to the position just past the last paragraph collapsed the
    /// selection to a caret instead of extending it.
    /// </para>
    /// <para>
    /// Which way to snap matters at a block boundary, where a text position lies on either side.
    /// An extending move prefers the direction it is travelling, so dragging down past the end
    /// of a paragraph reaches into the next one rather than stopping short.
    /// </para>
    /// </remarks>
    /// <param name="position">The document position to move to.</param>
    /// <param name="extend">Whether to keep the current anchor.</param>
    public void MoveTo(int position, bool extend)
    {
        // Once the selection is a rectangle its Anchor is the position before a cell, which is
        // in the row rather than in any cell's text. Resolving that back to a cell finds nothing,
        // so the text position the drag started from is remembered separately and is what both
        // the rectangle and the fall back to text are measured from.
        var origin = _state.Selection is CellSelection current
            ? CellOrigin(current)
            : _state.Selection.Anchor;

        var head = NearestTextPosition(position, forward: !extend || position >= origin);
        var anchor = extend ? origin : head;

        var selection = extend && CellSelection.Across(_state.Doc, anchor, head) is { } rectangle
            ? rectangle
            : (Selection)TextSelection.Create(_state.Doc, anchor, head);

        if (selection == _state.Selection)
        {
            // Still reset the blink: a caret that will not move should be solid, not caught
            // mid-blink, or the key reads as dropped.
            ResetCaretBlink();
            return;
        }

        _cellOrigin = anchor;
        State = EditorState.Create(_state.Doc, selection);
        ResetCaretBlink();
    }

    /// <summary>
    /// The position nearest a given one at which text lives.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>Selection.Near</c>, which stops at the first <em>selectable</em>
    /// position and so answers a rule with a node selection rather than searching past it for
    /// somewhere a text caret can go. Node selections are how a rule will be selected once
    /// there is a gesture that produces one; a caret movement is not that gesture.
    /// </remarks>
    /// <param name="position">Where the caret was asked to go.</param>
    /// <param name="forward">Which way to prefer when both directions are equally close.</param>
    private int NearestTextPosition(int position, bool forward)
    {
        var size = _state.Doc.ContentSize;
        var start = Math.Clamp(position, 0, size);

        for (var distance = 0; distance <= size; distance++)
        {
            var first = forward ? start + distance : start - distance;

            if (first >= 0 && first <= size && _state.Doc.Resolve(first).IsInText)
            {
                return first;
            }

            var second = forward ? start - distance : start + distance;

            if (distance > 0 && second >= 0 && second <= size && _state.Doc.Resolve(second).IsInText)
            {
                return second;
            }
        }

        // A document with no text at all — nothing but rules. Nothing here can help; the
        // selection machinery will make what it can of the raw position.
        return start;
    }

    /// <summary>
    /// The first position at which text lives, searching strictly in one direction.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NearestTextPosition"/>, which searches both ways and so is wrong
    /// for a caret <em>step</em>: pressing Right at the end of a paragraph aims at the boundary
    /// past it, and the position one before that boundary is nearer than the one after, so a
    /// nearest search walks the caret backwards into the block it just left.
    /// </remarks>
    /// <param name="position">Where to start looking, inclusive.</param>
    /// <param name="forward">Which way to look.</param>
    /// <returns>The position found, or the caret's current one when there is none that way.</returns>
    private int TextPositionToward(int position, bool forward)
    {
        var size = _state.Doc.ContentSize;
        var step = forward ? 1 : -1;

        for (var i = Math.Clamp(position, 0, size); i >= 0 && i <= size; i += step)
        {
            if (_state.Doc.Resolve(i).IsInText)
            {
                return i;
            }
        }

        // Nothing that way at all — a document of nothing but rules. Refusing to move is the
        // honest answer; no reachable input reaches this, since a caret can only be in a block
        // that has text and MoveTo re-snaps whatever comes back.
        return _state.Selection.Head;
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();

        var point = e.GetPosition(this);

        // Touch chrome belongs to touch. A mouse press on a hybrid device takes it away, or the
        // handles sit under a mouse caret with nothing that would ever remove them.
        if (e.Pointer.Type == PointerType.Touch)
        {
            // Before the caret: a handle hangs below the line and sits over the text under it, so
            // a press there would otherwise place a caret and drop the selection being adjusted.
            if (TryBeginTouchDrag(point))
            {
                e.Pointer.Capture(this);
                e.Handled = true;

                return;
            }

            ShowTouchHandles();
        }
        else
        {
            HideTouchHandles();
        }

        // A handle is checked before anything else: it sits outside the image's box, over the
        // text beside it, so a press there would otherwise place a caret and drop the selection
        // that put the handle on screen in the first place.
        if (TryBeginResize(point))
        {
            e.Pointer.Capture(this);
            e.Handled = true;

            return;
        }

        // Clicking a picture selects the picture, as it does everywhere else. Shift is left to
        // the text path so that a selection can still be extended across one.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && ImageUnder(point) is { } image
            && SelectImage(image))
        {
            e.Pointer.Capture(this);
            e.Handled = true;

            return;
        }

        _goalX = null;
        IsDragging = true;

        // Explicit, although Avalonia also captures implicitly on press — measured: deleting
        // this line changes nothing the headless harness can observe. It is kept because a drag
        // that leaves the control is exactly the case the harness cannot exercise, and that is
        // the case capture exists for.
        e.Pointer.Capture(this);

        MoveTo(PositionAt(point), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPointerMoved(e);

        var point = e.GetPosition(this);

        if (_touchDrag is not null)
        {
            UpdateTouchDrag(point);
            e.Handled = true;

            return;
        }

        if (_resizing is not null)
        {
            UpdateResize(point);
            e.Handled = true;

            return;
        }

        if (!_dragging)
        {
            // An I-beam everywhere says every part of the control is text, which is wrong for
            // the space below the last block and to the right of a short line. Only recomputed
            // when it changes: assigning Cursor raises a property change and re-reads the
            // pointer on every move otherwise.
            var wanted = ResizeCursor(point)
                ?? (IsOverContent(point) ? TextCursor : ArrowCursor);

            if (!ReferenceEquals(Cursor, wanted))
            {
                Cursor = wanted;
            }

            return;
        }

        // extend: true unconditionally. Once a drag has started the anchor is where it went
        // down, whether or not Shift is still held.
        MoveTo(PositionAt(point), extend: true);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        // The single place a drag ends. Releasing the button drops capture and arrives here, and
        // so does having capture taken away — a popup opening, the window deactivating. Clearing
        // the flag in a pointer-released handler as well would be duplicate bookkeeping that no
        // test could distinguish from this.
        IsDragging = false;
        EndResize();
        EndTouchDrag();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnKeyDown(e);

        if (e.Handled || Block is null)
        {
            return;
        }

        // Touch chrome does not survive the keyboard: a handle left under text the user is typing
        // into points at a selection that the next keystroke is about to replace.
        HideTouchHandles();

        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var word = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.Left:
                Horizontal(forward: false, word, extend);
                break;

            case Key.Right:
                Horizontal(forward: true, word, extend);
                break;

            case Key.Up:
                Vertical(down: false, extend);
                break;

            case Key.Down:
                Vertical(down: true, extend);
                break;

            case Key.Home:
                _goalX = null;
                MoveTo(word ? 0 : BlockStart + Block!.LineStart(CaretLine), extend);
                break;

            case Key.End:
                _goalX = null;
                MoveTo(
                    word ? _state.Doc.ContentSize : BlockStart + Block!.LineEnd(CaretLine),
                    extend);
                break;

            case Key.Enter:
                if (!Split())
                {
                    return;
                }

                break;

            case Key.Back:
                if (!Backspace())
                {
                    return;
                }

                break;

            case Key.Delete:
                if (!Delete())
                {
                    return;
                }

                break;

            case Key.Z when word && extend:
            case Key.Y when word:
                if (!Redo())
                {
                    return;
                }

                break;

            case Key.Z when word:
                if (!Undo())
                {
                    return;
                }

                break;

            case Key.A when word:
                SelectAll();
                break;

            // The clipboard is asynchronous, so these are started and not awaited. The key is
            // marked handled regardless: letting Ctrl+V fall through to text input would type a
            // literal "v" into the document while the paste was still in flight.
            case Key.C when word:
                _ = CopyAsync();
                break;

            // Cut deliberately refuses Shift: Ctrl+Shift+X is strikethrough, and a Cut that
            // ignored the modifier would swallow the chord and destroy the selection instead.
            case Key.X when word && !extend:
                _ = CutAsync();
                break;

            case Key.V when word:
                _ = PasteAsync();
                break;

            default:
                if (!MarkShortcut(e.Key, word, extend) && !BlockShortcut(e.Key, word, extend))
                {
                    return;
                }

                break;
        }

        e.Handled = true;
    }

    /// <summary>The number of positions in the realised block.</summary>
    private int ContentSize => Block?.ContentSize ?? 0;

    /// <summary>The caret's position expressed relative to the block.</summary>
    private int CaretLocal =>
        Math.Clamp(_state.Selection.Head - BlockStart, 0, ContentSize);

    /// <summary>
    /// The visual line the caret is <em>drawn</em> on.
    /// </summary>
    /// <remarks>
    /// A wrap boundary is one position on two lines, and the two answers are not equally good
    /// here. Measured on a paragraph wrapping at 72: the caret for position 72 is drawn at
    /// y = 15.96, which is the second line. So Home, End and vertical movement must all treat it
    /// as belonging to the line that <em>starts</em> there, or they disagree with what the user
    /// can see — pressing Up at a wrap boundary would leave for the top of the document, and
    /// Home would jump to the start of the previous line.
    /// </remarks>
    private int CaretLine => Block!.LineIndexAt(CaretLocal, forward: true);

    private void Horizontal(bool forward, bool word, bool extend)
    {
        _goalX = null;

        // A collapsing move — no Shift, and something is selected — goes to the edge of the
        // selection rather than one step from its head. Stepping instead would swallow a
        // character at one end, which is a bug users notice immediately.
        if (!extend && !_state.Selection.IsEmpty && !word)
        {
            MoveTo(forward ? _state.Selection.To : _state.Selection.From, extend: false);
            return;
        }

        var local = CaretLocal;

        var moved = word
            ? WordBoundary(local, forward)
            : forward ? Block!.NextCaretPosition(local) : Block!.PreviousCaretPosition(local);

        if (moved != local)
        {
            MoveTo(BlockStart + moved, extend);
            return;
        }

        // Nowhere left to go inside this block, so leave it. Aiming one position outside the
        // block and searching from there is what makes rules and nesting depth irrelevant here:
        // whatever lies between, the caret lands on the next place text actually is.
        var slot = _slots[_caretBlock];

        MoveTo(TextPositionToward(forward ? slot.End + 1 : slot.Start - 1, forward), extend);
    }

    private void Vertical(bool down, bool extend)
    {
        var local = CaretLocal;
        var line = CaretLine;
        var offset = BlockOffset;
        var caret = Block!.GetCaretRect(local);

        // The goal is kept in this control's coordinates, not the paragraph's. Two paragraphs in
        // two cells of a table start at different columns, so a paragraph-local goal would slide
        // sideways every time the caret crossed between them.
        _goalX ??= offset.X + caret.X;

        var target = down ? line + 1 : line - 1;

        if (target >= 0 && target < Block!.LineCount)
        {
            MoveTo(BlockStart + Block.PositionAtLineX(target, _goalX.Value - offset.X), extend);
            return;
        }

        // Off the end of this paragraph: continue into the nearest one past it at the same column,
        // which is the whole reason the goal column is kept across the move.
        var edge = offset.Y + (down ? caret.Bottom : caret.Y);

        if (AdjacentParagraph(_state.Selection.Head, edge, down, _goalX.Value) is not { } next)
        {
            // The first or last paragraph in the document. Go to the document edge, the way
            // every editor does, rather than refusing to move.
            MoveTo(down ? _state.Doc.ContentSize : 0, extend);
            return;
        }

        MoveTo(
            next.Start + next.View.PositionAtLineX(
                down ? 0 : next.View.LineCount - 1,
                _goalX.Value - next.Origin.X),
            extend);
    }

    /// <summary>
    /// The next word boundary in a direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deliberate approximation, not UAX #29: a word is a run of letters, digits, marks and
    /// underscores, and moving skips any separators before the run. Moving forward lands after
    /// the word, moving back lands before it, which is what Windows does.
    /// </para>
    /// <para>
    /// It walks text elements rather than chars so it cannot stop inside a surrogate pair or
    /// between a base character and its combining marks.
    /// </para>
    /// </remarks>
    private int WordBoundary(int local, bool forward)
    {
        var text = Block!.Paragraph.Content.Text;

        if (forward)
        {
            var i = local;

            while (i < text.Length && !IsWordChar(text, i))
            {
                i = Block.NextCaretPosition(i);
            }

            while (i < text.Length && IsWordChar(text, i))
            {
                i = Block.NextCaretPosition(i);
            }

            return i;
        }

        var j = local;

        while (j > 0 && !IsWordChar(text, Block.PreviousCaretPosition(j)))
        {
            j = Block.PreviousCaretPosition(j);
        }

        while (j > 0 && IsWordChar(text, Block.PreviousCaretPosition(j)))
        {
            j = Block.PreviousCaretPosition(j);
        }

        return j;
    }

    private static bool IsWordChar(string text, int index)
    {
        if (index < 0 || index >= text.Length)
        {
            return false;
        }

        var category = char.GetUnicodeCategory(text, index);

        return category is System.Globalization.UnicodeCategory.UppercaseLetter
            or System.Globalization.UnicodeCategory.LowercaseLetter
            or System.Globalization.UnicodeCategory.TitlecaseLetter
            or System.Globalization.UnicodeCategory.ModifierLetter
            or System.Globalization.UnicodeCategory.OtherLetter
            or System.Globalization.UnicodeCategory.DecimalDigitNumber
            or System.Globalization.UnicodeCategory.LetterNumber
            or System.Globalization.UnicodeCategory.OtherNumber
            or System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark
            or System.Globalization.UnicodeCategory.EnclosingMark
            or System.Globalization.UnicodeCategory.ConnectorPunctuation;
    }
}
