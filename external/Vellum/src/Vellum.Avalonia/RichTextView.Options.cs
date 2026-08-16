using Avalonia;

namespace Vellum.Avalonia;

/// <summary>
/// The four properties that say what the control will let a user do.
/// </summary>
/// <remarks>
/// <para>
/// Three of these exist on <c>TextBox</c> and one on <c>TextBox</c>'s undo stack, and a host that
/// already knows that control expects the names to mean the same things here. Measured against
/// Avalonia 12.1.1, <c>TextBox</c> defaults them to <c>IsReadOnly=false</c>,
/// <c>AcceptsReturn=false</c>, <c>AcceptsTab=false</c> and <c>UndoLimit=10</c>. Vellum keeps the
/// first and departs from the other three deliberately, for reasons given on each.
/// </para>
/// <para>
/// The important one is <see cref="IsReadOnly"/>, and the important thing about it is that it is
/// enforced in exactly one place. A read-only flag checked at each of the twenty-odd commands is
/// a flag that will be missed by the twenty-first, and the miss is silent: the document changes
/// and nobody notices until a user does. <see cref="RichTextView.Apply"/> is the only route by
/// which a transaction becomes the control's state, so the single check there covers typing,
/// paste, drop, delete, IME composition, every toolbar command, image resizing and anything added
/// later, whether or not its author remembered this file existed.
/// </para>
/// </remarks>
public partial class RichTextView
{
    /// <summary>Defines the <see cref="IsReadOnly"/> property.</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<RichTextView, bool>(nameof(IsReadOnly));

    /// <summary>Defines the <see cref="AcceptsReturn"/> property.</summary>
    public static readonly StyledProperty<bool> AcceptsReturnProperty =
        AvaloniaProperty.Register<RichTextView, bool>(nameof(AcceptsReturn), defaultValue: true);

    /// <summary>Defines the <see cref="AcceptsTab"/> property.</summary>
    public static readonly StyledProperty<bool> AcceptsTabProperty =
        AvaloniaProperty.Register<RichTextView, bool>(nameof(AcceptsTab), defaultValue: true);

    /// <summary>Defines the <see cref="UndoLimit"/> property.</summary>
    public static readonly StyledProperty<int> UndoLimitProperty =
        AvaloniaProperty.Register<RichTextView, int>(
            nameof(UndoLimit),
            defaultValue: HistoryPolicy.Unlimited,
            coerce: CoerceUndoLimit);

    /// <summary>Whether the document may be changed by the user.</summary>
    /// <remarks>
    /// <para>
    /// Read-only is about the user, not the host. Selecting, moving the caret, copying, finding
    /// and scrolling all still work - a document nobody can select is not read-only, it is
    /// inert - and setting <see cref="RichTextView.State"/> still replaces the document, because
    /// that is how a host loads one.
    /// </para>
    /// <para>
    /// Undo and redo are refused too. They are edits: a control that let Ctrl+Z run while
    /// read-only would let a user rewind past whatever the host had just installed.
    /// </para>
    /// <para>
    /// For a document that is never edited, prefer <see cref="RichTextViewer"/>. It is a smaller
    /// control that never builds the editing machinery in the first place; this property is for
    /// an editor that is read-only for now.
    /// </para>
    /// </remarks>
    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>Whether Enter splits the paragraph.</summary>
    /// <remarks>
    /// Defaults to true, where <c>TextBox</c> defaults to false, because <c>TextBox</c> is a
    /// single-line field by default and this is a document editor. Turning it off leaves Enter
    /// unhandled rather than swallowing it, so a dialog's default button still fires.
    /// </remarks>
    public bool AcceptsReturn
    {
        get => GetValue(AcceptsReturnProperty);
        set => SetValue(AcceptsReturnProperty, value);
    }

    /// <summary>Whether Tab indents, and nests a list item, rather than moving focus.</summary>
    /// <remarks>
    /// Defaults to true, where <c>TextBox</c> defaults to false. Tab means something specific in
    /// a document - indent, or nest the bullet - and a list a user cannot nest with the key every
    /// other editor uses for it is a list that is awkward for a small gain in focus navigation.
    /// Turning it off gives the key back to the focus system everywhere, including inside a list.
    /// </remarks>
    public bool AcceptsTab
    {
        get => GetValue(AcceptsTabProperty);
        set => SetValue(AcceptsTabProperty, value);
    }

    /// <summary>The most edits the undo stack will keep, or -1 for no limit.</summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see cref="HistoryPolicy.Unlimited"/>, where <c>TextBox</c> defaults to 10. Ten
    /// is far too few for a document, and any finite default would silently start discarding
    /// history that Vellum has kept since it had a history at all. A host that cares about the
    /// memory an unbroken session can hold should set this; each step owns the inverted steps that
    /// undo it, so a large paste is retained in full for as long as it is reachable.
    /// </para>
    /// <para>
    /// Zero disables undo. Lowering the limit trims the stacks at once rather than at the next
    /// edit, so a host that has just asked for two steps cannot immediately take five.
    /// </para>
    /// <para>
    /// A negative value other than -1 is coerced to -1 rather than throwing. It arrives through a
    /// style or a binding as often as through code, and a binding that throws during layout takes
    /// the window down.
    /// </para>
    /// </remarks>
    public int UndoLimit
    {
        get => GetValue(UndoLimitProperty);
        set => SetValue(UndoLimitProperty, value);
    }

    private static int CoerceUndoLimit(AvaloniaObject _, int value) =>
        value < 0 ? HistoryPolicy.Unlimited : value;

    /// <summary>Applies a changed <see cref="UndoLimit"/> to the live history.</summary>
    /// <remarks>
    /// The early return is what keeps this from fighting the <see cref="RichTextView.History"/>
    /// setter, which pushes an incoming policy's limit back into this property: without it, every
    /// assignment would replace the history the host just handed over with a rebuilt copy.
    /// </remarks>
    private void OnUndoLimitChanged(int limit)
    {
        if (limit == _history.Policy.Limit)
        {
            return;
        }

        var before = HistoryFlags;

        _history = _history.WithPolicy(_history.Policy with { Limit = limit });

        RaiseHistoryFlags(before);
    }
}
