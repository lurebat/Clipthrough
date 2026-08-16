using System.Text;

namespace Vellum.Interop.Html;

/// <summary>
/// Collects the blocks an import produces, and the run of inline content not yet closed into one.
/// </summary>
/// <remarks>
/// HTML draws no line between the two: text, an <c>&lt;em&gt;</c> and a <c>&lt;table&gt;</c> can be
/// siblings. So the walker pours everything into here, and a paragraph appears whenever something
/// arrives that cannot be part of one.
/// </remarks>
internal sealed class BlockSink
{
    private readonly List<BlockNode> _blocks = [];
    private readonly StringBuilder _text = new();
    private readonly List<ValueSpan<MarkSet>> _marks = [];
    private readonly List<InlineEmbed> _embeds = [];
    private bool _pendingSpace;
    private MarkSet _pendingSpaceMark;

    /// <summary>The blocks produced so far.</summary>
    internal IReadOnlyList<BlockNode> Blocks => _blocks;

    /// <summary>Whether any inline content is waiting to be closed into a paragraph.</summary>
    internal bool HasPending => _text.Length > 0;

    /// <summary>Adds text.</summary>
    /// <param name="text">The characters.</param>
    /// <param name="mark">Their formatting.</param>
    /// <param name="preformatted">Whether whitespace in it is content.</param>
    internal void Add(string text, MarkSet mark, bool preformatted)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (preformatted)
        {
            Append(text, mark);
            return;
        }

        // Outside <pre>, any run of whitespace is one space, and a run at the start of a paragraph
        // is nothing at all. Deferring the space rather than appending it is what makes the run at
        // the end disappear too: nothing ever asks for it.
        var start = -1;

        for (var i = 0; i < text.Length; i++)
        {
            if (IsCollapsible(text[i]))
            {
                if (start >= 0)
                {
                    Append(text[start..i], mark);
                    start = -1;
                }

                DeferSpace(mark);
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0)
        {
            Append(text[start..], mark);
        }
    }

    /// <summary>
    /// Records that a space is owed, carrying the formatting in force where it occurred.
    /// </summary>
    /// <param name="mark">The formatting of the whitespace itself.</param>
    /// <remarks>
    /// <para>
    /// The mark has to be captured here rather than taken from whatever text arrives next. In
    /// <c>x &lt;b&gt;bold&lt;/b&gt;</c> the space is outside the element, so charging it to the
    /// following run makes the bold span one character too wide — and formatting then creeps a
    /// character to the left every time a document is imported.
    /// </para>
    /// <para>
    /// The first of two adjacent whitespace runs wins, because that is the one that survives the
    /// collapse. In <c>&lt;b&gt;bold &lt;/b&gt; text</c> the surviving space is inside the
    /// element and is therefore bold, which is what HTML itself says.
    /// </para>
    /// </remarks>
    private void DeferSpace(MarkSet mark)
    {
        if (_pendingSpace)
        {
            return;
        }

        _pendingSpace = true;
        _pendingSpaceMark = mark;
    }

    /// <summary>Adds an embedded object, which occupies exactly one position.</summary>
    /// <param name="embed">The embed.</param>
    /// <param name="mark">Its formatting.</param>
    internal void AddEmbed(InlineEmbed embed, MarkSet mark)
    {
        FlushPendingSpace();
        AppendRaw(InlineContent.Placeholder.ToString(), mark);
        _embeds.Add(embed);
    }

    /// <summary>Adds a line break inside the paragraph being built.</summary>
    /// <param name="mark">The formatting in force where the break occurred.</param>
    /// <remarks>
    /// A break swallows the whitespace on either side of it, which is why this is not simply a
    /// preformatted newline: "a &lt;br&gt; b" is two lines reading "a" and "b", not "a " and " b".
    /// </remarks>
    internal void AddLineBreak(MarkSet mark)
    {
        _pendingSpace = false;
        Append("\n", mark);
        _pendingSpace = false;
    }

    /// <summary>Whether a character is whitespace that HTML collapses.</summary>
    /// <param name="c">The character.</param>
    /// <returns>True if a run of it becomes a single space.</returns>
    /// <remarks>
    /// Deliberately not <see cref="char.IsWhiteSpace(char)"/>, which is true of U+00A0. A
    /// non-breaking space is content — it is exactly what a source writes when it wants a space
    /// that survives — and Word fills its output with them.
    /// </remarks>
    private static bool IsCollapsible(char c) =>
        c is ' ' or '\t' or '\n' or '\r' or '\f';

    private void Append(string text, MarkSet mark)
    {
        FlushPendingSpace();

        // U+FFFC belongs to the model: it marks where an embed sits, and the model rejects content
        // whose placeholder count does not match its embed count. Source text is entitled to
        // contain the character anyway — Word writes it for objects it cannot otherwise express,
        // and anything that has already round-tripped through a rich text model will carry it — so
        // it has to be taken out here. Letting one through discards the whole import.
        if (text.Contains(InlineContent.Placeholder, StringComparison.Ordinal))
        {
            text = text.Replace(InlineContent.Placeholder.ToString(), string.Empty, StringComparison.Ordinal);

            if (text.Length == 0)
            {
                return;
            }
        }

        AppendRaw(text, mark);
    }

    private void FlushPendingSpace()
    {
        if (!_pendingSpace)
        {
            return;
        }

        _pendingSpace = false;

        // A space only separates things. There is nothing to the left of the first one, and
        // nothing to the left of the first one on a line either.
        if (_text.Length > 0 && _text[^1] != '\n')
        {
            AppendRaw(" ", _pendingSpaceMark);
        }
    }

    private void AppendRaw(string text, MarkSet mark)
    {
        var start = _text.Length;
        _text.Append(text);

        if (mark.IsEmpty)
        {
            return;
        }

        // Runs that carry the same formatting are merged as they arrive. The model would reject
        // overlapping spans and merge equal neighbours anyway, but doing it here keeps a paragraph
        // built from a thousand nested spans from allocating a thousand of them first.
        if (_marks.Count > 0)
        {
            var last = _marks[^1];

            if (last.End == start && last.Value == mark)
            {
                _marks[^1] = new ValueSpan<MarkSet>(last.Start, last.Length + text.Length, mark);
                return;
            }
        }

        _marks.Add(new ValueSpan<MarkSet>(start, text.Length, mark));
    }

    /// <summary>Closes any pending inline content into a paragraph.</summary>
    /// <param name="style">The formatting for the paragraph.</param>
    /// <param name="force">
    /// Whether to produce a paragraph even with nothing in it, which is what an explicit line break
    /// asks for.
    /// </param>
    internal void Flush(BlockStyle style, bool force = false)
    {
        if (_text.Length == 0 && !force)
        {
            Reset();
            return;
        }

        var content = InlineContent.Create(_text.ToString(), _marks, _embeds);

        _blocks.Add(new ParagraphNode(content, style.Kind, style.Align, style.Indent));
        Reset();
    }

    /// <summary>Closes any pending content, then adds a block of its own.</summary>
    /// <param name="style">The formatting for the paragraph being closed.</param>
    /// <param name="block">The block to add.</param>
    internal void AddBlock(BlockStyle style, BlockNode block)
    {
        Flush(style);
        _blocks.Add(block);
    }

    private void Reset()
    {
        _text.Clear();
        _marks.Clear();
        _embeds.Clear();
        _pendingSpace = false;
    }
}
