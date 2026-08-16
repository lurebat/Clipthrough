using System.Collections.Immutable;

namespace Vellum;

/// <summary>
/// What kind of edit a transaction was, for the benefit of undo grouping.
/// </summary>
/// <remarks>
/// History needs to know why a change happened, not just what changed. Twenty keystrokes should
/// undo as one word; a paste should never be swallowed into the keystrokes on either side of it.
/// Nothing else in the model consults this - it is a note the editor leaves for history.
/// </remarks>
public enum TransactionKind
{
    /// <summary>Characters entered one at a time.</summary>
    Typing,

    /// <summary>Backspace, Delete, or removing a selection.</summary>
    Delete,

    /// <summary>Bold, colour, alignment - anything that changes appearance.</summary>
    Format,

    /// <summary>Splitting, joining, lists, tables - anything that changes the shape.</summary>
    Structure,

    /// <summary>Content arriving from outside.</summary>
    Paste,

    /// <summary>Composition in progress. Provisional until the input method commits it.</summary>
    Ime,

    /// <summary>Undo or redo replaying recorded steps.</summary>
    History,
}

/// <summary>
/// A change to the editor, assembled before it is applied.
/// </summary>
/// <remarks>
/// <para>
/// A transaction is built by adding steps to it, each applied immediately so that the next can be
/// expressed against the document as it now stands. That is what makes "delete the selection,
/// then type the character" writable as two independent steps rather than one fused operation
/// that has to do the arithmetic itself.
/// </para>
/// <para>
/// A step that cannot be applied is dropped and recorded in <see cref="Failures"/> rather than
/// throwing. Steps fail for ordinary reasons - a range whose two ends are in different blocks -
/// and a command that composes several of them should be able to try one and carry on.
/// </para>
/// </remarks>
public sealed class Transaction
{
    private readonly ImmutableArray<Step>.Builder _steps = ImmutableArray.CreateBuilder<Step>();

    private readonly ImmutableArray<DocumentNode>.Builder _docs =
        ImmutableArray.CreateBuilder<DocumentNode>();

    private readonly ImmutableArray<string>.Builder _failures =
        ImmutableArray.CreateBuilder<string>();

    private Mapping _mapping = Mapping.Empty;
    private Selection? _selection;
    private MarkSet? _storedMarks;
    private bool _storedMarksSet;
    private bool _changedContent;
    private bool _sealed;

    /// <summary>Starts a transaction from a state.</summary>
    /// <param name="before">The state to change.</param>
    public Transaction(EditorState before)
    {
        ArgumentNullException.ThrowIfNull(before);

        Before = before;
        Doc = before.Doc;
    }

    /// <summary>The state this transaction started from.</summary>
    public EditorState Before { get; }

    /// <summary>The document as it stands with the steps so far applied.</summary>
    public DocumentNode Doc { get; private set; }

    /// <summary>The steps that were applied, in order.</summary>
    public ImmutableArray<Step> Steps => _steps.ToImmutable();

    /// <summary>
    /// The document each step was applied to, in order. What inverting the steps needs.
    /// </summary>
    public ImmutableArray<DocumentNode> DocsBefore => _docs.ToImmutable();

    /// <summary>Why each dropped step was dropped.</summary>
    public ImmutableArray<string> Failures => _failures.ToImmutable();

    /// <summary>How the whole transaction moves positions.</summary>
    public Mapping Mapping => _mapping;

    /// <summary>Whether any step was applied.</summary>
    public bool ChangedDoc => _steps.Count > 0;

    /// <summary>What kind of edit this is.</summary>
    public TransactionKind Kind { get; set; } = TransactionKind.Structure;

    /// <summary>
    /// When this edit happened, for undo grouping.
    /// </summary>
    /// <remarks>
    /// Settable so that tests and replayed history are not at the mercy of the wall clock.
    /// </remarks>
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The selection after this transaction: the one that was set, or the old one moved forward.
    /// </summary>
    /// <remarks>
    /// An explicitly set selection is still checked against the finished document. Taking it as
    /// given means not moving it, not trusting it: a caller that sets a selection and then adds
    /// a step that deletes the ground under it would otherwise hand the editor a caret outside
    /// the document.
    /// </remarks>
    public Selection SelectionAfter => _selection is { } set
        ? set.Map(Doc, Mapping.Empty)
        : Before.Selection.Map(Doc, _mapping);

    /// <summary>
    /// The formatting armed for the next character typed, or null if there is none.
    /// </summary>
    /// <remarks>
    /// Pressing Ctrl+B with a caret arms boldness that has nowhere to live yet. It has to survive
    /// until something is typed and be dropped the moment anything is, which is why it belongs to
    /// the state rather than to the document. Changing existing formatting or a block attribute
    /// is not something being typed, so it leaves the armed formatting alone.
    /// </remarks>
    public MarkSet? StoredMarks =>
        _storedMarksSet ? _storedMarks
        : _changedContent ? null
        : Before.StoredMarks;

    /// <summary>Applies a step, or records why it could not be applied.</summary>
    /// <param name="step">The step.</param>
    /// <returns>This transaction, for chaining.</returns>
    /// <exception cref="InvalidOperationException">This transaction has been recorded.</exception>
    public Transaction Step(Step step)
    {
        ArgumentNullException.ThrowIfNull(step);
        RequireOpen();

        var result = step.Apply(Doc);

        if (!result.IsOk)
        {
            _failures.Add(result.Failure!);

            return this;
        }

        // A step that applied cleanly and left the very same document did nothing. Recording it
        // would put an entry on the undo stack that undoes nothing, which reads to the user as
        // an undo that did not work.
        if (ReferenceEquals(result.Doc, Doc))
        {
            return this;
        }

        _steps.Add(step);
        _docs.Add(Doc);

        var map = step.GetMap();

        _mapping = _mapping.Append(map);
        _changedContent |= !map.IsIdentity;
        Doc = result.Doc!;

        return this;
    }

    /// <summary>Replaces a range with a slice.</summary>
    /// <param name="from">Where the range starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="slice">What to put there.</param>
    public Transaction Replace(int from, int to, Slice slice) =>
        Step(new ReplaceStep(from, to, slice));

    /// <summary>Removes a range.</summary>
    /// <param name="from">Where the range starts.</param>
    /// <param name="to">Where it ends.</param>
    public Transaction Delete(int from, int to) => Step(ReplaceStep.Delete(from, to));

    /// <summary>Removes a range whose two ends need not sit at the same depth.</summary>
    /// <remarks>
    /// <para>
    /// A single <see cref="ReplaceStep"/> cannot express this. Its two ends have to close back up
    /// at one level, so it refuses a range running from inside a list item into a plain paragraph
    /// — which is what a user produces by selecting across a bullet, and what Ctrl+A produces in
    /// any document that has a list or a table in it. That refusal is correct for the step and
    /// useless as an answer to the user, so the range is taken apart here into pieces each of
    /// which <em>is</em> one step.
    /// </para>
    /// <para>
    /// There are four, emitted last first so that each one lands on positions the ones after it
    /// have not moved: the block holding the end of the range goes entirely, carrying with it any
    /// ancestor it leaves empty; the whole blocks in between go; whatever survived after the range
    /// inside that last block is re-inserted at the range's start; and the range's own tail is
    /// removed from the block it started in. The result keeps the <em>first</em> block's
    /// structure, so selecting from a bullet into a paragraph and deleting leaves a bullet.
    /// </para>
    /// </remarks>
    /// <param name="from">Where the range starts.</param>
    /// <param name="to">Where it ends.</param>
    public Transaction DeleteRange(int from, int to)
    {
        RequireOpen();

        if (from < 0 || to > Doc.ContentSize || to <= from)
        {
            return Delete(from, to);
        }

        var start = Doc.Resolve(from);
        var end = Doc.Resolve(to);

        // Equal depths are what a plain step already handles, and a position sitting directly in
        // the document has no block of its own to take apart.
        if (start.Depth == end.Depth || start.Depth == 0 || end.Depth == 0)
        {
            return Delete(from, to);
        }

        var tail = end.Paragraph is { } paragraph
            ? paragraph.Content.Substring(end.ParentOffset, paragraph.Content.Length - end.ParentOffset)
            : InlineContent.Empty;

        // How much of the last block goes: the node holding the range's end, and then every
        // ancestor that node was the only child of, because a list with no items left in it is
        // not a document the schema will accept.
        var depth = end.Depth;

        while (depth > 1 && end.NodeAt(depth - 1).Children.Count == 1)
        {
            depth--;
        }

        var contentEnd = start.End(start.Depth);

        Delete(end.Before(depth), end.After(depth));

        // Nothing lies between the two blocks when the range never left one, and an empty range
        // is not a step: it would rebuild the document into an equal copy and put an undo entry
        // on the stack that undoes nothing.
        if (start.After(1) < end.Before(1))
        {
            Delete(start.After(1), end.Before(1));
        }

        if (tail.Length > 0)
        {
            Replace(contentEnd, contentEnd, Slice.OfInline(tail));
        }

        return from < contentEnd ? Delete(from, contentEnd) : this;
    }

    /// <summary>Inserts text.</summary>
    /// <param name="pos">Where to insert.</param>
    /// <param name="text">What to insert.</param>
    /// <param name="mark">The formatting to give it.</param>
    public Transaction InsertText(int pos, string text, MarkSet mark = default) =>
        Step(ReplaceStep.Insert(pos, Slice.OfInline(InlineContent.FromText(text, mark))));

    /// <summary>Where the selection should be once this transaction is applied.</summary>
    /// <param name="selection">The selection.</param>
    /// <remarks>
    /// Set this after the steps. It is taken as given rather than mapped, so a selection set
    /// before a later step will not follow that step's changes.
    /// </remarks>
    public Transaction SetSelection(Selection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        RequireOpen();

        _selection = selection;

        return this;
    }

    /// <summary>Arms formatting for the next character typed.</summary>
    /// <param name="marks">The formatting, or null to disarm.</param>
    public Transaction SetStoredMarks(MarkSet? marks)
    {
        RequireOpen();

        _storedMarks = marks;
        _storedMarksSet = true;

        return this;
    }

    /// <summary>Records what kind of edit this is.</summary>
    /// <param name="kind">The kind.</param>
    public Transaction As(TransactionKind kind)
    {
        RequireOpen();
        Kind = kind;

        return this;
    }

    /// <summary>Records when this edit happened.</summary>
    /// <param name="time">The time.</param>
    public Transaction At(DateTimeOffset time)
    {
        RequireOpen();
        Time = time;

        return this;
    }

    /// <summary>
    /// Closes this transaction to further change.
    /// </summary>
    /// <remarks>
    /// History reads a transaction once, at the moment it is recorded, and keeps the inverse it
    /// derived. If the same transaction were then extended and applied, the document would move
    /// further than the undo entry knows how to move it back, and undo would silently leave the
    /// later edit behind. Sealing turns that into an exception at the point of the mistake.
    /// </remarks>
    internal void Seal() => _sealed = true;

    private void RequireOpen()
    {
        if (_sealed)
        {
            throw new InvalidOperationException(
                "This transaction has already been recorded in history and cannot be changed. "
                + "Begin a new transaction for further edits.");
        }
    }

    /// <summary>
    /// The steps that undo this transaction: reversed, and each inverted against the document it
    /// was applied to.
    /// </summary>
    public ImmutableArray<Step> Invert()
    {
        var inverted = ImmutableArray.CreateBuilder<Step>(_steps.Count);

        for (var i = _steps.Count - 1; i >= 0; i--)
        {
            inverted.Add(_steps[i].Invert(_docs[i]));
        }

        return inverted.ToImmutable();
    }
}

/// <summary>
/// Everything the editor knows: a document, a selection, and any armed formatting.
/// </summary>
/// <remarks>
/// The state is immutable and replaced wholesale. Nothing edits a document in place, so there is
/// never a moment when the selection refers to a document that no longer exists, and any state
/// can be kept and compared with any other - which is what undo, and later collaboration, need.
/// </remarks>
public sealed record EditorState
{
    private EditorState(DocumentNode doc, Selection selection, MarkSet? storedMarks)
    {
        Doc = doc;
        Selection = selection;
        StoredMarks = storedMarks;
    }

    /// <summary>The document.</summary>
    public DocumentNode Doc { get; }

    /// <summary>What is selected.</summary>
    public Selection Selection { get; }

    /// <summary>Formatting armed for the next character typed, if any.</summary>
    public MarkSet? StoredMarks { get; }

    /// <summary>A state holding a document, with the caret at the first place it can sit.</summary>
    /// <param name="doc">The document.</param>
    public static EditorState Create(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        return new EditorState(doc, Vellum.Selection.AtStart(doc), null);
    }

    /// <summary>A state holding a document and a selection.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="selection">What is selected.</param>
    public static EditorState Create(DocumentNode doc, Selection selection)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(selection);

        return new EditorState(doc, selection, null);
    }

    /// <summary>Begins a transaction against this state.</summary>
    public Transaction Transaction() => new(this);

    /// <summary>The state this one becomes once a transaction is applied.</summary>
    /// <param name="tr">The transaction.</param>
    /// <exception cref="ArgumentException">The transaction was built against another state.</exception>
    public EditorState Apply(Transaction tr)
    {
        ArgumentNullException.ThrowIfNull(tr);

        if (!ReferenceEquals(tr.Before, this))
        {
            throw new ArgumentException(
                "The transaction was built against a different state. Its positions are measured "
                + "against that state's document and mean nothing here.",
                nameof(tr));
        }

        return new EditorState(tr.Doc, tr.SelectionAfter, tr.StoredMarks);
    }
}
