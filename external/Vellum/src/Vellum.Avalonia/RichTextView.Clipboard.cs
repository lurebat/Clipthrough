using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;

namespace Vellum.Avalonia;

/// <summary>
/// Selecting everything, and moving text through the system clipboard.
/// </summary>
/// <remarks>
/// <para>
/// Plain text always works. The richer flavours architecture section 4.9 describes — HTML and RTF
/// — arrive by registration: this assembly deliberately does not reference the interop packages,
/// because an application that wants a text editor should not acquire an HTML parser and an RTF
/// reader to get one. Adding <c>HtmlFormat.Instance</c> and <c>RtfFormat.Instance</c> to
/// <see cref="Formats"/> is what turns them on, and nothing else changes.
/// </para>
/// <para>
/// A paste tries the registered formats in <see cref="RichTextClipboard.Preference"/>
/// order and stops at the first that yields a document, so a paste from a browser keeps its
/// formatting and a paste from a terminal still arrives as text.
/// </para>
/// <para>
/// The clipboard is asynchronous on every platform Avalonia supports, so these are tasks. The
/// keyboard and menu paths deliberately do not await them: a paste that has to wait on another
/// process must not freeze the caret.
/// </para>
/// </remarks>
public partial class RichTextView
{
    /// <summary>
    /// Refuses a format object that can neither be read nor written, at the point of registration
    /// rather than silently doing nothing at the point of use.
    /// </summary>
    private sealed class DocumentFormats : System.Collections.ObjectModel.Collection<IDocumentFormat>
    {
        protected override void InsertItem(int index, IDocumentFormat item)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (item is not (IDocumentImporter or IDocumentExporter))
            {
                throw new ArgumentException(
                    $"'{item.Format}' can neither be read nor written: it implements neither " +
                    $"{nameof(IDocumentImporter)} nor {nameof(IDocumentExporter)}.",
                    nameof(item));
            }

            base.InsertItem(index, item);
        }
    }

    /// <summary>The interchange formats copy writes and paste reads, beyond plain text.</summary>
    /// <remarks>
    /// A format object usually implements both <see cref="IDocumentImporter"/> and
    /// <see cref="IDocumentExporter"/> — <c>HtmlFormat.Instance</c> and <c>RtfFormat.Instance</c>
    /// do — but either alone is accepted, so an application can offer a format it can read
    /// without claiming to write it. A format implementing neither is refused, loudly, at the
    /// point of registration rather than silently doing nothing at the point of use.
    /// </remarks>
    public ICollection<IDocumentFormat> Formats { get; } = new DocumentFormats();

    /// <summary>Selects the whole document.</summary>
    /// <returns><see langword="true"/> if the selection changed.</returns>
    public bool SelectAll()
    {
        var from = NearestTextPosition(0, forward: true);
        var to = NearestTextPosition(_state.Doc.ContentSize, forward: false);

        if (_state.Selection is TextSelection current && current.From == from && current.To == to)
        {
            return false;
        }

        _goalX = null;
        State = EditorState.Create(_state.Doc, TextSelection.Create(_state.Doc, from, to));
        ResetCaretBlink();

        return true;
    }

    /// <summary>The plain text covered by the selection, empty when the selection is a caret.</summary>
    public string SelectedText()
    {
        var selection = _state.Selection;

        if (selection.IsEmpty)
        {
            return string.Empty;
        }

        if (selection is CellSelection rectangle)
        {
            return DocumentText.Of(CellDocument(rectangle).Blocks);
        }

        return DocumentText.Of(Slice.Cut(_state.Doc, selection.From, selection.To).Content);
    }

    /// <summary>Puts the selection on the clipboard, in every registered format.</summary>
    /// <returns><see langword="true"/> if anything was written.</returns>
    public async Task<bool> CopyAsync()
    {
        var text = SelectedText();

        if (text.Length == 0)
        {
            return false;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetDataAsync(BuildCopyData(text)).ConfigureAwait(true);

        return true;
    }

    /// <summary>
    /// The clipboard payload for the current selection: plain text, plus a flavour for each
    /// registered exporter that has one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CopyAsync"/> because it is the whole of what a copy decides, and
    /// the only part of it a test can look at: what a real clipboard does with the payload
    /// afterwards is the platform's business, not this control's.
    /// </remarks>
    internal DataTransfer BuildCopyData(string text)
    {
        var item = new DataTransferItem();

        item.SetText(text);

        // The document the flavours describe is the selection, not the whole document — copying a
        // paragraph out of the middle must not paste the document back in.
        var doc = SelectedDocument();

        foreach (var exporter in Formats.OfType<IDocumentExporter>())
        {
            if (RichTextClipboard.FlavorFor(exporter.Format) is not { } flavour)
            {
                continue;
            }

            try
            {
                item.Set(flavour.DataFormat, flavour.Encoding.GetBytes(exporter.Export(doc)));
            }
            catch (Exception)
            {
                // An exporter is contractually forbidden from throwing, so this is a bug in one.
                // Losing that flavour is the right price; losing the user's copy is not.
            }
        }

        var data = new DataTransfer();

        data.Add(item);

        return data;
    }

    /// <summary>The selection as a document in its own right.</summary>
    internal DocumentNode SelectedDocument()
    {
        var selection = _state.Selection;

        // Cutting the range a rectangle bounds would take every cell between its first and its
        // last, which for a column selection is the whole table. The cells are rebuilt instead.
        if (selection is CellSelection rectangle)
        {
            return CellDocument(rectangle);
        }

        var slice = Slice.Cut(_state.Doc, selection.From, selection.To);

        return new DocumentNode(slice.Content.OfType<BlockNode>());
    }

    /// <summary>Puts the selection on the clipboard and removes it from the document.</summary>
    /// <returns><see langword="true"/> if anything was cut.</returns>
    public async Task<bool> CutAsync()
    {
        // The range is settled before the await, and the delete refuses if the selection moved
        // while the clipboard was busy. Awaiting first and deleting "the selection" afterwards
        // cuts whatever the user happened to select in the meantime.
        var before = _state.Selection;

        if (before.IsEmpty || !await CopyAsync().ConfigureAwait(true))
        {
            return false;
        }

        return DeleteCutRange(before);
    }

    /// <summary>
    /// Removes the range a cut was started on, refusing if the selection moved meanwhile.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CutAsync"/> because it is the only part of a cut that can be
    /// tested: the headless clipboard completes synchronously, so no test can drive the await
    /// this exists to defend.
    /// </remarks>
    internal bool DeleteCutRange(Selection before)
    {
        var after = _state.Selection;

        if (after.From != before.From || after.To != before.To)
        {
            return false;
        }

        return DeleteSelection();
    }

    /// <summary>Replaces the selection with the clipboard's richest readable flavour.</summary>
    /// <returns><see langword="true"/> if the document changed.</returns>
    public async Task<bool> PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        try
        {
            using var data = await clipboard.TryGetDataAsync().ConfigureAwait(true);
            if (data is null)
            {
                return false;
            }

            foreach (var importer in ReadableFormats())
            {
                if (RichTextClipboard.FlavorFor(importer.Format) is not { } flavour)
                {
                    continue;
                }
                // Bytes, deliberately, and never as a string: a clipboard backend handed a native
                // format to decode as text will guess an encoding, and its guess for RTF produces
                // convincing mojibake that then parses to nothing.
                if (await data.TryGetValueAsync(flavour.DataFormat).ConfigureAwait(true) is not { Length: > 0 } bytes)
                {
                    continue;
                }

                var doc = importer.Import(flavour.Encoding.GetString(bytes)).Doc;

                if (!doc.Blocks.IsEmpty)
                {
                    return PasteDocument(doc);
                }
            }

            // After the rich formats and before plain text. A screenshot on the clipboard often
            // carries a text flavour too - a filename, or nothing useful - so taking text first
            // would paste the label instead of the picture.
            if (await InsertImageFrom(data).ConfigureAwait(true))
            {
                return true;
            }

            var text = await data.TryGetTextAsync().ConfigureAwait(true);

            return text is { Length: > 0 } && PasteText(text);
        }
        catch (Exception)
        {
            // A clipboard owned by another process can fail for reasons none of which are this
            // control's business. Losing a paste is bad; taking the application down is worse.
            return false;
        }
    }

    /// <summary>The registered importers, richest first.</summary>
    /// <remarks>
    /// Internal because the order is the whole behaviour: an application that copies from Word
    /// gets RTF, HTML and plain text at once, and taking whichever answers first would silently
    /// paste the poorest of the three.
    /// </remarks>
    internal IEnumerable<IDocumentImporter> ReadableFormats() =>
        RichTextClipboard.InPreferenceOrder(Formats.OfType<IDocumentImporter>());

    /// <summary>Replaces the selection with <paramref name="doc"/>'s blocks.</summary>
    /// <param name="doc">The document to insert.</param>
    /// <returns><see langword="true"/> if the document changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> is <see langword="null"/>.</exception>
    public bool PasteDocument(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (doc.Blocks.IsEmpty || Rectangle is not null)
        {
            return false;
        }

        // Open ends are what make the first and last pasted paragraphs merge into the paragraph
        // the caret sits in, instead of pushing it apart — but only a paragraph can be entered
        // that way. Content that begins or ends with something else gets an empty open paragraph
        // at that end, which does two jobs at once: it splits the host paragraph so a table or a
        // list has somewhere to land, and it carries the text on that side of the caret back out.
        // Without it the step is simply refused, and the paste is lost in silence.
        var blocks = doc.Blocks;

        if (blocks[0] is not ParagraphNode)
        {
            blocks = blocks.Insert(0, Empty);
        }

        if (blocks[^1] is not ParagraphNode)
        {
            blocks = blocks.Add(Empty);
        }

        // Merging is right when there is something to merge with: pasting a word into a sentence
        // must not bring the source paragraph's heading style along. Into an empty paragraph there
        // is nothing to preserve, and merging would instead throw the pasted block's own kind
        // away — pasting a heading and getting body text. So that case replaces the host outright,
        // with the original blocks: the padding above exists only to open a slice, and a closed
        // one would keep it as a stray empty paragraph.
        return EmptyHostBlock() is { } host
            ? PasteSlice(new Slice([.. doc.Blocks], 0, 0), host.From, host.To)
            : PasteSlice(new Slice([.. blocks], 1, 1));
    }

    /// <summary>
    /// The range of the block the selection sits in, when that block is an empty paragraph and so
    /// has nothing worth merging into.
    /// </summary>
    private (int From, int To)? EmptyHostBlock()
    {
        var selection = _state.Selection;

        if (!selection.IsEmpty)
        {
            return null;
        }

        var at = _state.Doc.Resolve(selection.From);

        if (at.Depth < 1 || at.NodeAt(at.Depth) is not ParagraphNode { Content.Text.Length: 0 })
        {
            return null;
        }

        return (at.Before(at.Depth), at.After(at.Depth));
    }

    private static ParagraphNode Empty => new(InlineContent.Empty);

    /// <summary>Replaces the selection with <paramref name="text"/>, honouring line breaks.</summary>
    /// <param name="text">The text to insert.</param>
    /// <returns><see langword="true"/> if the document changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public bool PasteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0 || Rectangle is not null)
        {
            return false;
        }

        // Normalized first, so that a Windows clipboard's CRLF does not paste two breaks and a
        // classic-Mac CR does not paste none.
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var selection = _state.Selection;
        var at = selection.From;

        // One transaction, so that one undo takes the paste back. Splitting and inserting line by
        // line reads more simply and is wrong: it leaves the user pressing Ctrl+Z once per line.
        // Open on both ends is what makes the first and last pasted paragraphs merge into the
        // paragraph the caret was sitting in, rather than pushing it apart.
        var content = lines.Select(line => (Node)new ParagraphNode(InlineContent.FromText(line))).ToArray();

        return PasteSlice(new Slice(content, 1, 1));
    }

    /// <summary>Replaces the selection with <paramref name="slice"/>.</summary>
    /// <remarks>
    /// One transaction, so that one undo takes the paste back. Splitting and inserting block by
    /// block reads more simply and is wrong: it leaves the user pressing Ctrl+Z once per block.
    /// </remarks>
    private bool PasteSlice(Slice slice) =>
        PasteSlice(slice, _state.Selection.From, _state.Selection.To);

    /// <summary>Replaces <paramref name="from"/>..<paramref name="to"/> with <paramref name="slice"/>.</summary>
    private bool PasteSlice(Slice slice, int from, int to)
    {
        var transaction = _state.Transaction().As(TransactionKind.Paste);

        // A range whose ends are at different depths is not one replacement, so the removal has
        // to happen first and the slice go into the caret it leaves. Doing this only when the
        // depths differ keeps the ordinary paste one step, which is what makes its open edges
        // join to what surrounds them rather than landing as separate blocks.
        if (from < to && _state.Doc.Resolve(from).Depth != _state.Doc.Resolve(to).Depth)
        {
            transaction.DeleteRange(from, to);

            if (!transaction.Failures.IsEmpty)
            {
                return false;
            }

            to = from;
        }

        transaction
            .Replace(from, to, slice)
            .SetSelection(TextSelection.Cursor(from + slice.Size))
            .SetStoredMarks(null);

        return Apply(transaction);
    }

    /// <summary>Removes the selection, leaving a caret where it was.</summary>
    /// <returns><see langword="true"/> if the document changed.</returns>
    public bool DeleteSelection()
    {
        var selection = _state.Selection;

        if (selection.IsEmpty)
        {
            return false;
        }

        // Emptying the cells, not removing them: deleting the range a rectangle bounds takes
        // whole cells out of their rows, and for a column selection takes cells beside it too.
        if (selection is CellSelection rectangle)
        {
            return ClearCells(rectangle);
        }

        var transaction = _state.Transaction().As(TransactionKind.Structure);

        transaction
            .DeleteRange(selection.From, selection.To)
            .SetSelection(TextSelection.Cursor(selection.From))
            .SetStoredMarks(null);

        return Apply(transaction);
    }
}