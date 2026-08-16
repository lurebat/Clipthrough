using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Vellum.Avalonia;

/// <summary>
/// Touch selection handles, per architecture 10.3 P7.
/// </summary>
/// <remarks>
/// <para>
/// A finger covers the text it is pointing at, so a touch selection cannot be adjusted by dragging
/// the text itself the way a mouse selection is. Two grab handles are drawn below the ends of the
/// selection instead, and dragging one moves that end while the other stays put.
/// </para>
/// <para>
/// The handles belong to touch and to nothing else. They appear on a touch press and are taken
/// away by a mouse press or a keystroke, because a hybrid device has both and leaving touch chrome
/// under a mouse caret is clutter that nothing will ever remove.
/// </para>
/// </remarks>
public partial class RichTextView
{
    /// <summary>Defines the <see cref="TouchHandleBrush"/> property.</summary>
    public static readonly StyledProperty<IBrush?> TouchHandleBrushProperty =
        AvaloniaProperty.Register<RichTextView, IBrush?>(
            nameof(TouchHandleBrush), new SolidColorBrush(Color.FromRgb(0, 120, 215)));

    /// <summary>Defines the <see cref="AreTouchHandlesVisible"/> property.</summary>
    public static readonly DirectProperty<RichTextView, bool> AreTouchHandlesVisibleProperty =
        AvaloniaProperty.RegisterDirect<RichTextView, bool>(
            nameof(AreTouchHandlesVisible), o => o.AreTouchHandlesVisible);

    /// <summary>The radius of the circle a handle is drawn as, in device-independent pixels.</summary>
    public const double TouchHandleRadius = 7;

    /// <summary>How far from a handle's centre a touch still counts as grabbing it.</summary>
    /// <remarks>
    /// Deliberately much larger than <see cref="TouchHandleRadius"/>. A fingertip is about 9mm
    /// across and lands where the user is not looking, because their finger is over it; a target
    /// the size of the drawn circle is one nobody can hit. This is the radius rather than the side,
    /// so the target is a circle of roughly 44 device-independent pixels across.
    /// </remarks>
    public const double TouchHandleTargetRadius = 22;

    private bool _touchHandles;

    /// <summary>Which end of the selection is being dragged, or null: 0 its start, 1 its end.</summary>
    private int? _touchDrag;

    /// <summary>Whether the drag is of the caret handle rather than of a selection end.</summary>
    private bool _touchCaret;

    /// <summary>The document position the drag is holding still.</summary>
    private int _touchFixed;

    /// <summary>
    /// From the point the finger went down to the caret it grabbed.
    /// </summary>
    /// <remarks>
    /// Kept so the selection follows the same place in the handle it was grabbed by. Hit-testing
    /// the raw touch point instead puts the end of the selection a whole handle below where the
    /// user is aiming, which on a short paragraph is the next line and on the last one is nothing.
    /// </remarks>
    private Vector _touchGrab;

    /// <summary>The fill of a touch selection handle.</summary>
    public IBrush? TouchHandleBrush
    {
        get => GetValue(TouchHandleBrushProperty);
        set => SetValue(TouchHandleBrushProperty, value);
    }

    /// <summary>Whether the touch selection handles are being drawn.</summary>
    public bool AreTouchHandlesVisible
    {
        get => _touchHandles;
        private set
        {
            if (SetAndRaise(AreTouchHandlesVisibleProperty, ref _touchHandles, value))
            {
                InvalidateVisual();
            }
        }
    }

    /// <summary>Whether a selection end is being dragged by a touch handle.</summary>
    public bool IsDraggingTouchHandle => _touchDrag is not null;

    /// <summary>
    /// Where the handles are drawn, in this control's coordinates.
    /// </summary>
    /// <remarks>
    /// One circle for a caret, two for a selection: index 0 sits under
    /// <see cref="Selection.From"/> and index 1 under <see cref="Selection.To"/>. Both come from
    /// the caret rectangle at that position rather than from the selection rectangles, which is
    /// what makes them right across a bidi boundary — the logical start of a selection that
    /// crosses one is on the right of the run, and the selection's leftmost rectangle is not it.
    /// </remarks>
    public IReadOnlyList<Rect> TouchHandles
    {
        get
        {
            if (!_touchHandles)
            {
                return [];
            }

            var selection = _state.Selection;

            // No handles on a rectangle of cells. A handle is the end of a text range that
            // dragging it moves, and a rectangle has no such ends: its bounds are positions in
            // a row, and dragging one would extend a text selection through cells beside it.
            if (selection is CellSelection)
            {
                return [];
            }

            if (selection.IsEmpty)
            {
                return HandleAt(selection.Head) is { } caret ? [caret] : [];
            }

            return HandleAt(selection.From) is { } from && HandleAt(selection.To) is { } to
                ? [from, to]
                : [];
        }
    }

    /// <summary>Shows the touch selection handles.</summary>
    public void ShowTouchHandles() => AreTouchHandlesVisible = true;

    /// <summary>Takes the touch selection handles away.</summary>
    /// <remarks>Ends a drag in progress, so hiding them cannot strand one.</remarks>
    public void HideTouchHandles()
    {
        EndTouchDrag();
        AreTouchHandlesVisible = false;
    }

    /// <summary>The circle drawn for the handle at a document position, or null.</summary>
    private Rect? HandleAt(int position)
    {
        if (PositionRect(position) is not { } rect)
        {
            return null;
        }

        // Centred on the caret and hung below the line, so the handle points at the position it
        // adjusts without covering the character there.
        return new Rect(
            rect.X - TouchHandleRadius,
            rect.Bottom,
            TouchHandleRadius * 2,
            TouchHandleRadius * 2);
    }

    /// <summary>The caret rectangle at a document position, in this control's coordinates.</summary>
    private Rect? PositionRect(int position)
    {
        if (ParagraphAt(position) is not { } at)
        {
            return null;
        }

        var local = at.View.GetCaretRect(
            Math.Clamp(position - at.Start, 0, at.View.ContentSize));

        return local.Translate(new Vector(at.Origin.X, at.Origin.Y));
    }

    /// <summary>Starts a drag if a finger went down on a handle.</summary>
    /// <remarks>
    /// <para>
    /// The nearest handle within the target radius wins rather than the first one hit. At a
    /// one-character selection the two targets overlap almost completely, and taking the first
    /// would mean the end handle could never be grabbed there.
    /// </para>
    /// <para>
    /// The caret handle is draggable too, and moves the caret rather than extending from it. It is
    /// the only way to place a caret precisely on a touch screen, where the finger covers the
    /// character being aimed at.
    /// </para>
    /// </remarks>
    private bool TryBeginTouchDrag(Point point)
    {
        var handles = TouchHandles;

        if (handles.Count == 0)
        {
            return false;
        }

        var best = -1;
        var bestDistance = TouchHandleTargetRadius;

        for (var i = 0; i < handles.Count; i++)
        {
            var distance = Distance(handles[i].Center, point);

            if (distance <= bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        if (best < 0)
        {
            return false;
        }

        var selection = _state.Selection;
        var moving = best == 0 ? selection.From : selection.To;

        _touchDrag = best;
        _touchCaret = handles.Count == 1;
        _touchFixed = best == 0 ? selection.To : selection.From;
        _touchGrab = PositionRect(moving) is { } rect
            ? new Vector(rect.Center.X - point.X, rect.Center.Y - point.Y)
            : default;

        // The anchor has to be the end that is staying put before the first move arrives, or the
        // first MoveTo would extend from whichever end the selection happened to be anchored at
        // and drag the wrong one.
        State = EditorState.Create(_state.Doc, TextSelection.Create(_state.Doc, _touchFixed, moving));

        // Anything showing an affordance for the selection waits for the drag to finish, exactly
        // as it does for a mouse drag. See IsDragging.
        IsDragging = true;

        return true;
    }

    /// <summary>Moves the end of the selection a handle is holding.</summary>
    /// <remarks>
    /// Dragging one end past the other is allowed to happen: the anchor stays where it was put, so
    /// the selection simply turns around and the same finger keeps controlling the same end of it.
    /// Refusing to cross would leave the selection stuck at a zero width with the finger somewhere
    /// else entirely.
    /// </remarks>
    private void UpdateTouchDrag(Point point) =>
        MoveTo(PositionAt(point + _touchGrab), extend: !_touchCaret);

    /// <summary>Ends a handle drag, if one is in progress.</summary>
    private void EndTouchDrag()
    {
        if (_touchDrag is null)
        {
            return;
        }

        _touchDrag = null;
        _touchCaret = false;
        _touchGrab = default;

        InvalidateVisual();
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private void RenderTouchHandles(DrawingContext context)
    {
        if (TouchHandleBrush is not { } brush)
        {
            return;
        }

        var pen = new ImmutablePen(brush.ToImmutable(), 1);

        foreach (var handle in TouchHandles)
        {
            // A stem up to the line, so the handle reads as pointing at a position between two
            // characters rather than floating under one of them.
            context.DrawLine(
                pen,
                new Point(handle.Center.X, handle.Top - TouchHandleRadius),
                handle.Center);

            context.DrawEllipse(brush, null, handle.Center, TouchHandleRadius, TouchHandleRadius);
        }
    }
}
