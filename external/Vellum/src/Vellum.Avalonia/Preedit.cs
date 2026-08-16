namespace Vellum.Avalonia;

/// <summary>
/// Text an input method is composing but has not yet committed, per architecture 4.8.
/// </summary>
/// <remarks>
/// <para>
/// <b>A preedit is not content.</b> It never becomes a <see cref="Transaction"/>, never reaches
/// the document and never reaches the undo stack. It is drawn by splicing extra runs into the
/// paragraph's layout and it disappears when the input method either commits — as one ordinary
/// typing transaction — or cancels. Getting this wrong is the classic rich-text bug: Ctrl+Z
/// replaying half-composed Japanese.
/// </para>
/// <para>
/// <see cref="Offset"/> is paragraph-local, in the same space as every other position that
/// crosses <see cref="ParagraphView"/>'s boundary.
/// </para>
/// </remarks>
public sealed record Preedit
{
    /// <summary>Creates a composition overlay.</summary>
    /// <param name="text">The text being composed. Must not be empty.</param>
    /// <param name="offset">Where in the paragraph it is being composed.</param>
    /// <param name="cursorPosition">
    /// Where the input method wants the caret, as an offset into <paramref name="text"/>, or
    /// null to leave it at the end.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> is negative, or <paramref name="cursorPosition"/> falls outside
    /// the composed text.
    /// </exception>
    public Preedit(string text, int offset, int? cursorPosition = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        // An empty composition is indistinguishable from no composition, and letting one exist
        // would mean every consumer has to handle a zero-length overlay that changes nothing.
        if (text.Length == 0)
        {
            throw new ArgumentException("A preedit with no text is no preedit.", nameof(text));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (cursorPosition is { } cursor && (cursor < 0 || cursor > text.Length))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cursorPosition),
                cursor,
                $"The composition cursor must be within [0, {text.Length}].");
        }

        Text = text;
        Offset = offset;
        CursorPosition = cursorPosition;
    }

    /// <summary>The text being composed.</summary>
    public string Text { get; }

    /// <summary>Where in the paragraph it is being composed.</summary>
    public int Offset { get; }

    /// <summary>Where the input method wants the caret within <see cref="Text"/>.</summary>
    public int? CursorPosition { get; }

    /// <summary>The paragraph-local position the caret should be drawn at.</summary>
    /// <remarks>
    /// In the layout's coordinates, not the document's: the composed text occupies positions
    /// the document does not have.
    /// </remarks>
    public int CaretPosition => Offset + (CursorPosition ?? Text.Length);

    /// <summary>The number of UTF-16 code units the composition adds to the layout.</summary>
    public int Length => Text.Length;
}
