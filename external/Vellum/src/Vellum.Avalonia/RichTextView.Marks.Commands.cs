using Avalonia.Input;

namespace Vellum.Avalonia;

/// <summary>
/// Character formatting: bold, italic, colour, font, links and everything else a
/// <see cref="MarkSet"/> carries.
/// </summary>
/// <remarks>
/// <para>
/// Every command here is one call to <see cref="SetMarks"/>, which is one
/// <see cref="AddMarkStep"/>. A mark step already spans every paragraph a range touches, so a
/// selection running across three paragraphs and a list is still a single step and a single
/// undo entry.
/// </para>
/// <para>
/// A collapsed caret does not edit the document. It sets the state's stored marks instead, so
/// that pressing Ctrl+B and then typing produces bold text — and it deliberately records no
/// undo entry, because there is nothing to undo until a character is typed.
/// </para>
/// </remarks>
public partial class RichTextView
{
    /// <summary>The formatting the whole selection shares.</summary>
    /// <remarks>
    /// Only what <em>every</em> character has: a selection that is half bold reports not bold,
    /// and one spanning red and blue text reports no colour. That is what a toolbar needs in
    /// order to show bold as off for a mixed selection rather than lying about it.
    /// <para>
    /// For a collapsed caret this is the formatting the next typed character would get — the
    /// stored marks if there are any, and otherwise the formatting to the left of the caret.
    /// </para>
    /// </remarks>
    public MarkSet SelectionMarks
    {
        get
        {
            var selection = _state.Selection;

            if (selection is CellSelection rectangle)
            {
                return CellMarks(rectangle) ?? MarkSet.Empty;
            }

            return selection.IsEmpty
                ? MarkForInsertion(selection.From)
                : MarksAcross(selection.From, selection.To)
                    ?? MarkForInsertion(selection.From);
        }
    }

    /// <summary>Whether every character of the selection has all of <paramref name="style"/>.</summary>
    /// <param name="style">The styles to look for.</param>
    public bool IsActive(TextStyle style) => SelectionMarks.Has(style);

    /// <summary>Turns a style on if the selection does not already have it, and off if it does.</summary>
    /// <remarks>
    /// A partly bold selection becomes wholly bold rather than wholly unbold, which is what
    /// every other editor does and what users expect.
    /// </remarks>
    /// <param name="style">The style to toggle.</param>
    /// <returns>Whether anything changed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="style"/> is <see cref="TextStyle.None"/> or has unknown bits.
    /// </exception>
    public bool ToggleStyle(TextStyle style) => SetStyle(style, !IsActive(style));

    /// <summary>Turns a style on or off across the selection.</summary>
    /// <param name="style">The style to set.</param>
    /// <param name="on">Whether to turn it on.</param>
    /// <returns>Whether anything changed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="style"/> is <see cref="TextStyle.None"/> or has unknown bits.
    /// </exception>
    public bool SetStyle(TextStyle style, bool on)
    {
        if (style == TextStyle.None || (style & ~TextStyle.All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(style), style, "Not a style, or not a known one.");
        }

        // Only the requested bits are in the field mask, so turning Super on leaves every other
        // style alone. MarkSet.Apply is what stops Sub and Super ending up on together.
        return SetMarks(new MarkSet(style: on ? style : TextStyle.None), style.ToFields());
    }

    /// <summary>Sets the text colour across the selection.</summary>
    /// <param name="colour">The colour, or null to inherit.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetForeground(Rgba? colour) =>
        SetMarks(new MarkSet(foreground: colour), MarkFields.Foreground);

    /// <summary>Sets the highlight colour across the selection.</summary>
    /// <param name="colour">The colour, or null for no highlight.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetHighlight(Rgba? colour) =>
        SetMarks(new MarkSet(highlight: colour), MarkFields.Highlight);

    /// <summary>Sets the font family across the selection.</summary>
    /// <param name="family">The family name, or null or blank to inherit the theme's.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetFontFamily(string? family) =>
        SetMarks(new MarkSet(fontFamily: family), MarkFields.FontFamily);

    /// <summary>Sets the font size across the selection.</summary>
    /// <param name="size">The size in device-independent pixels, or null to inherit.</param>
    /// <returns>Whether anything changed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="size"/> is not a positive finite number.
    /// </exception>
    public bool SetFontSize(double? size) =>
        SetMarks(new MarkSet(fontSize: size), MarkFields.FontSize);

    /// <summary>Makes the selection a hyperlink, or removes one.</summary>
    /// <remarks>
    /// A collapsed caret only arms the link for the next typed character. Turning a caret into
    /// a link with visible text is an insert, not a formatting change, and belongs to the
    /// caller that knows what text to insert.
    /// </remarks>
    /// <param name="link">The link, or null to remove it.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetLink(LinkMark? link) =>
        SetMarks(new MarkSet(link: link), MarkFields.Link);

    /// <summary>Strips character formatting from the selection.</summary>
    /// <remarks>
    /// Hyperlinks survive, because clearing formatting should not silently throw away a URL
    /// that nothing else in the document records. Use <see cref="SetLink"/> for that.
    /// </remarks>
    /// <returns>Whether anything changed.</returns>
    public bool ClearFormatting() =>
        SetMarks(MarkSet.Empty, MarkFields.All & ~MarkFields.Link);

    /// <summary>Sets the chosen fields of the selection's formatting to those of a mark set.</summary>
    /// <remarks>
    /// The single primitive the rest of this file is written in terms of. Fields that are not
    /// named are left alone, so setting a colour does not disturb bold.
    /// </remarks>
    /// <param name="value">Supplies the new values for the named fields.</param>
    /// <param name="fields">Which fields to take from <paramref name="value"/>.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetMarks(MarkSet value, MarkFields fields)
    {
        if (fields == MarkFields.None)
        {
            return false;
        }

        var selection = _state.Selection;

        if (selection is CellSelection rectangle)
        {
            return MarkCells(rectangle, value, fields);
        }

        if (selection.IsEmpty)
        {
            return ArmStoredMarks(MarkForInsertion(selection.From).Apply(value, fields));
        }

        var transaction = _state.Transaction().As(TransactionKind.Format);

        // No "is it already set" check on purpose. A half-bold selection reports not bold, so
        // any such check would refuse to unbold it. The transaction drops a step that left the
        // document identical, which handles the genuinely redundant case without guessing.
        //
        // Clearing the stored marks is belt-and-braces and is deliberately untested: every
        // public way of ending up with a non-empty selection already clears them, so there is
        // no path that can observe the difference today. It stays because a future one would
        // otherwise leave marks armed for a caret waiting behind an edit to a range.
        transaction
            .Step(new AddMarkStep(selection.From, selection.To, value, fields))
            .SetSelection(selection)
            .SetStoredMarks(null);

        return Apply(transaction);
    }

    /// <summary>Handles the keyboard shortcuts for character formatting.</summary>
    /// <remarks>
    /// Reports whether the chord was <em>recognised</em>, not whether it changed anything. A
    /// chord the control claims has to be swallowed either way, or pressing it in a state where
    /// it happens to be a no-op would let it fall through to whatever else is listening — and
    /// Ctrl+Space is a space away from the text input handler.
    /// </remarks>
    private bool MarkShortcut(Key key, bool control, bool shift)
    {
        switch ((key, control, shift))
        {
            case (Key.B, true, false):
                ToggleStyle(TextStyle.Bold);
                return true;

            case (Key.I, true, false):
                ToggleStyle(TextStyle.Italic);
                return true;

            case (Key.U, true, false):
                ToggleStyle(TextStyle.Underline);
                return true;

            case (Key.X, true, true):
                ToggleStyle(TextStyle.Strikethrough);
                return true;

            case (Key.OemPeriod, true, false):
                ToggleStyle(TextStyle.Super);
                return true;

            case (Key.OemComma, true, false):
                ToggleStyle(TextStyle.Sub);
                return true;

            case (Key.Space, true, false):
                ClearFormatting();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Sets the formatting the next typed character will get, without touching the document.
    /// </summary>
    /// <remarks>
    /// Deliberately not routed through <see cref="Apply"/>. That would refuse the transaction
    /// for changing no text, and recording it would put an entry on the undo stack that undoes
    /// nothing the user can see.
    /// </remarks>
    private bool ArmStoredMarks(MarkSet marks)
    {
        if (_state.StoredMarks == marks)
        {
            return false;
        }

        State = _state.Apply(_state.Transaction().SetStoredMarks(marks));

        return true;
    }

    /// <summary>
    /// The formatting shared by every character in a range, or null if it contains none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the leaf blocks rather than the top-level ones, because a paragraph inside a table
    /// cell is not a top-level block. Reading only the top level finds no characters at all
    /// inside a table and falls back to the formatting at the selection's start, which reports a
    /// half-bold cell as bold and makes Ctrl+B take the bold off instead of putting it on.
    /// </para>
    /// <para>
    /// The slot list is rebuilt first if it is missing. Dropping a layout — a new resolver, a
    /// font change on the control — empties it until the next measure pass, and reading it
    /// empty would report no characters at all. The caller would then fall back to the
    /// formatting at the selection's start, so a half-bold selection beginning in bold would
    /// answer "bold" and Ctrl+B would take the bold off instead of putting it on.
    /// </para>
    /// </remarks>
    private MarkSet? MarksAcross(int from, int to)
    {
        EnsureSlots();

        MarkSet? common = null;

        foreach (var slot in Leaves)
        {
            if (slot.Node is not ParagraphNode paragraph)
            {
                continue;
            }

            var start = Math.Max(from, slot.Start);
            var end = Math.Min(to, slot.End);

            for (var offset = start; offset < end; offset++)
            {
                var mark = paragraph.Content.MarkAt(offset - slot.Start);

                common = common is { } far ? Intersect(far, mark) : mark;
            }
        }

        return common;
    }

    /// <summary>Keeps only what two mark sets agree on.</summary>
    private static MarkSet Intersect(MarkSet a, MarkSet b) => new()
    {
        Style = a.Style & b.Style,
        FontFamily = a.FontFamily == b.FontFamily ? a.FontFamily : null,
        FontSize = a.FontSize == b.FontSize ? a.FontSize : null,
        Foreground = a.Foreground == b.Foreground ? a.Foreground : null,
        Highlight = a.Highlight == b.Highlight ? a.Highlight : null,
        Link = a.Link == b.Link ? a.Link : null,
    };
}