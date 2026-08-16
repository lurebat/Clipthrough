using Avalonia.Input;

namespace Vellum.Avalonia;

/// <summary>
/// Commands that change what a block <em>is</em> rather than what it says: its kind, its
/// alignment and its indent level.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these applies to every block the selection touches, not just the one the caret
/// is in, because that is what a user selecting three paragraphs and pressing the heading button
/// means. A block the change cannot legally apply to is skipped rather than failing the whole
/// transaction, so selecting a mixed range and centring it centres what can be centred.
/// </para>
/// <para>
/// The steps are collected into one transaction, so one press is one undo.
/// </para>
/// </remarks>
public partial class RichTextView
{
    /// <summary>The most a block may be indented.</summary>
    /// <remarks>
    /// A limit rather than none, because indent multiplies into the wrap width and a document
    /// that indented without bound would leave paragraphs a single character wide. Eight levels
    /// is past anything a reader can follow.
    /// </remarks>
    public const int MaxIndentLevel = 8;

    /// <summary>Sets the kind of every paragraph the selection touches.</summary>
    /// <param name="kind">The kind to set.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetParagraphKind(ParagraphKind kind) =>
        SetBlockAttr(BlockAttr.ParagraphKind, (int)kind);

    /// <summary>Turns a paragraph kind on or off over the selection.</summary>
    /// <param name="kind">The kind to toggle.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// <para>
    /// A selection that is already entirely of this kind goes back to <see cref="ParagraphKind.Body"/>;
    /// anything else becomes this kind. That is what a button which stays lit has to mean — a
    /// heading button that only ever sets leaves no way back to body text except a second button,
    /// and the lit state becomes a display that cannot be turned off.
    /// </para>
    /// <para>
    /// A mixed selection takes the kind rather than clearing it, matching the lit state: the
    /// button is not lit, so pressing it turns the kind on. Toggling
    /// <see cref="ParagraphKind.Body"/> is just setting it, since body text is what off means —
    /// no guard is needed for that, because both branches yield body when body is what was asked
    /// for.
    /// </para>
    /// </remarks>
    public bool ToggleParagraphKind(ParagraphKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of paragraph.");
        }

        return SetParagraphKind(ParagraphKindAt == kind ? ParagraphKind.Body : kind);
    }

    /// <summary>Sets the alignment of every paragraph the selection touches.</summary>
    /// <param name="align">The alignment to set.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetAlign(TextAlign align) => SetBlockAttr(BlockAttr.Align, (int)align);

    /// <summary>Sets the indent level of every paragraph the selection touches.</summary>
    /// <param name="level">The level to set, clamped to <c>[0, <see cref="MaxIndentLevel"/>]</c>.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetIndentLevel(int level) =>
        SetBlockAttr(BlockAttr.IndentLevel, Math.Clamp(level, 0, MaxIndentLevel));

    /// <summary>Indents every paragraph the selection touches by one level.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool Indent() => ShiftIndent(1);

    /// <summary>Outdents every paragraph the selection touches by one level.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool Outdent() => ShiftIndent(-1);

    /// <summary>The kind every paragraph the selection touches shares, or null if they differ.</summary>
    /// <remarks>
    /// The same rule <see cref="SelectionMarks"/> follows: only what <em>every</em> one of them
    /// has. A selection running from a heading into body text reports null rather than picking
    /// one, so a toolbar shows nothing lit instead of lying about which it is.
    /// </remarks>
    public ParagraphKind? ParagraphKindAt =>
        Shared(BlockAttr.ParagraphKind) is { } value ? (ParagraphKind)value : null;

    /// <summary>The alignment every paragraph the selection touches shares, or null if they differ.</summary>
    public TextAlign? AlignAt => Shared(BlockAttr.Align) is { } value ? (TextAlign)value : null;

    /// <summary>The indent level every paragraph the selection touches shares, or null if they differ.</summary>
    public int? IndentLevelAt => Shared(BlockAttr.IndentLevel);

    /// <summary>The value of an attribute across the selection, when every paragraph agrees.</summary>
    /// <remarks>
    /// Blocks that are not paragraphs are skipped rather than counted as disagreeing, matching
    /// the commands: a selection covering a paragraph and an image reports the paragraph's kind,
    /// because that is the only thing the heading button would change. A selection with no
    /// paragraph in it at all reports null, since there is nothing to agree.
    /// </remarks>
    private int? Shared(BlockAttr attr)
    {
        int? shared = null;

        foreach (var index in SelectedBlocks())
        {
            if (_slots[index].Node is not ParagraphNode paragraph)
            {
                continue;
            }

            var value = Current(paragraph, attr);

            if (shared is { } already && already != value)
            {
                return null;
            }

            shared = value;
        }

        return shared;
    }

    /// <summary>The block indices the selection touches, in document order.</summary>
    /// <remarks>
    /// A collapsed caret touches the one block it is in. A range touches every block it overlaps
    /// but not one it merely ends at the start of, which is the same rule the selection
    /// highlight uses — a selection that stops where a paragraph begins has not reached it.
    /// </remarks>
    private IEnumerable<int> SelectedBlocks()
    {
        // The slot list can be empty even though the document is not: replacing a resolver drops
        // the layout, and a host that then issues a command before the next measure pass would
        // otherwise get a silent no-op.
        EnsureSlots();

        // A rectangle of cells is not a run of blocks. Its From..To covers the whole table, so
        // a heading or an indent asked for it would land on the table rather than on anything
        // the reader selected — and there is no one block for it to land on instead.
        if (_state.Selection is CellSelection)
        {
            yield break;
        }

        var from = _state.Selection.From;
        var to = _state.Selection.To;

        for (var i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];

            if (from == to ? i == _caretBlock : to > slot.Start && from < slot.End)
            {
                yield return i;
            }
        }
    }

    private bool ShiftIndent(int by)
    {
        var transaction = _state.Transaction().As(TransactionKind.Format);
        var any = false;

        foreach (var index in SelectedBlocks())
        {
            if (_slots[index].Node is not ParagraphNode paragraph)
            {
                continue;
            }

            var level = Math.Clamp(paragraph.IndentLevel + by, 0, MaxIndentLevel);

            if (level != paragraph.IndentLevel)
            {
                transaction.Step(new SetBlockAttrStep(
                    _slots[index].Start - 1, BlockAttr.IndentLevel, level));
                any = true;
            }
        }

        return any && Apply(transaction);
    }

    private bool SetBlockAttr(BlockAttr attr, int value)
    {
        var transaction = _state.Transaction().As(TransactionKind.Format);
        var any = false;

        foreach (var index in SelectedBlocks())
        {
            if (_slots[index].Node is not ParagraphNode paragraph || Current(paragraph, attr) == value)
            {
                // Already what it is being set to. Stepping anyway would apply cleanly and
                // record an undo entry that undoes nothing, which is worse than doing nothing.
                continue;
            }

            // A block's own position is one before its content starts. The step addresses the
            // block, not the text inside it.
            transaction.Step(new SetBlockAttrStep(_slots[index].Start - 1, attr, value));
            any = true;
        }

        return any && Apply(transaction);
    }

    private static int Current(ParagraphNode paragraph, BlockAttr attr) => attr switch
    {
        BlockAttr.ParagraphKind => (int)paragraph.Kind,
        BlockAttr.Align => (int)paragraph.Align,
        BlockAttr.IndentLevel => paragraph.IndentLevel,
        _ => throw new ArgumentOutOfRangeException(
            nameof(attr), attr, "Not an attribute a paragraph carries."),
    };

    /// <summary>Handles the keyboard shortcuts for block attributes.</summary>
    /// <remarks>
    /// Returns rather than throwing on an unknown chord so the key handler can fall through to
    /// everything else. Tab is deliberately indent rather than a literal tab: a rich text
    /// control that swallowed Tab to insert whitespace would trap keyboard focus.
    /// </remarks>
    internal bool BlockShortcut(Key key, bool control, bool shift) => (key, control, shift) switch
    {
        (Key.Tab, false, var lift) => Tab(lift),

        (Key.D0, true, false) => SetParagraphKind(ParagraphKind.Body),
        (Key.D1, true, false) => ToggleParagraphKind(ParagraphKind.Heading1),
        (Key.D2, true, false) => ToggleParagraphKind(ParagraphKind.Heading2),
        (Key.D3, true, false) => ToggleParagraphKind(ParagraphKind.Heading3),
        (Key.D4, true, false) => ToggleParagraphKind(ParagraphKind.Heading4),
        (Key.D5, true, false) => ToggleParagraphKind(ParagraphKind.Heading5),
        (Key.D6, true, false) => ToggleParagraphKind(ParagraphKind.Heading6),

        (Key.D7, true, true) => ToggleList(ListKind.Ordered),
        (Key.D8, true, true) => ToggleList(ListKind.Unordered),

        (Key.L, true, false) => SetAlign(TextAlign.Left),
        (Key.E, true, false) => SetAlign(TextAlign.Center),
        (Key.R, true, false) => SetAlign(TextAlign.Right),
        (Key.J, true, false) => SetAlign(TextAlign.Justify),

        _ => false,
    };

    /// <summary>Handles Tab and Shift+Tab, which mean different things inside a list.</summary>
    /// <returns>Whether the key was consumed, which is not the same as whether anything changed.</returns>
    /// <remarks>
    /// <para>
    /// In a list, Tab changes the nesting rather than the indent: a bullet that moved right
    /// without becoming a sub-bullet would look nested and behave as though it were not.
    /// </para>
    /// <para>
    /// The first item of a list has nothing to nest under, so nesting there is impossible. It is
    /// still consumed, because both alternatives are worse: indenting produces exactly the
    /// misleading bullet the previous paragraph rules out, and reporting the key unhandled sends
    /// Tab on to focus navigation, so pressing it in a list would move focus out of the editor.
    /// </para>
    /// <para>
    /// Shift+Tab needs no such guard. Lifting always has somewhere to go — out of a nested list
    /// into the one around it, or out of a top-level list into ordinary paragraphs — so
    /// <see cref="LiftListItem"/> only fails when there is no list at all.
    /// </para>
    /// </remarks>
    private bool Tab(bool lift)
    {
        // Off, the key belongs to focus navigation - including inside a list, where the remarks
        // above otherwise argue for consuming it. A host that turned this off asked for that.
        if (!AcceptsTab)
        {
            return false;
        }

        if (ListKindAt is null)
        {
            return lift ? Outdent() : Indent();
        }

        _ = lift ? LiftListItem() : NestListItem();
        return true;
    }
}
