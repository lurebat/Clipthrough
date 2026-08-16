using System.Collections.Immutable;

namespace Vellum;

/// <summary>
/// One undoable unit: the steps that reverse it, and enough about how it happened to decide
/// whether the next edit belongs to it.
/// </summary>
/// <param name="Undo">The steps that reverse it, in the order they must be applied.</param>
/// <param name="SelectionBefore">Where the selection was before it happened.</param>
/// <param name="SelectionAfter">Where the selection ended up.</param>
/// <param name="Kind">What kind of edit it was.</param>
/// <param name="Time">When it happened.</param>
/// <param name="ChangedFrom">The lowest position it touched.</param>
/// <param name="ChangedTo">The highest position it touched, in the resulting document.</param>
public sealed record HistoryEvent(
    ImmutableArray<Step> Undo,
    Selection SelectionBefore,
    Selection SelectionAfter,
    TransactionKind Kind,
    DateTimeOffset Time,
    int ChangedFrom,
    int ChangedTo)
{
    /// <inheritdoc/>
    public bool Equals(HistoryEvent? other) =>
        other is not null
        && Undo.AsSpan().SequenceEqual(other.Undo.AsSpan())
        && SelectionBefore == other.SelectionBefore
        && SelectionAfter == other.SelectionAfter
        && Kind == other.Kind
        && Time == other.Time
        && ChangedFrom == other.ChangedFrom
        && ChangedTo == other.ChangedTo;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        Undo.Length,
        SelectionBefore,
        SelectionAfter,
        Kind,
        Time,
        ChangedFrom,
        ChangedTo);
}

/// <summary>
/// When two adjacent edits should undo as one, and how many edits are kept.
/// </summary>
/// <param name="Window">How long after an edit a following one may still join it.</param>
/// <param name="Enabled">Whether to group at all. Off makes every transaction its own step.</param>
/// <param name="Limit">
/// The most undo steps to keep, or <see cref="Unlimited"/> for no bound. Zero records nothing.
/// </param>
/// <remarks>
/// <para>
/// Grouping is a policy, not a mechanism. The mechanism - inverted steps on a stack - works just
/// as well undoing one keystroke at a time; it is only that nobody wants that. Keeping the rule in
/// one small value means it can be changed, or switched off in a test, without touching anything
/// that records history.
/// </para>
/// <para>
/// All the conditions have to hold, and the selection one is the one that gets forgotten: typing
/// "abc", clicking somewhere else, and typing "def" is two edits even though both are typing and
/// both happened inside half a second. Undoing them together would move text the reader never saw
/// move as one action.
/// </para>
/// <para>
/// <see cref="Limit"/> is a separate concern that lives here because it is the other thing a host
/// wants to say about a history, and putting it here keeps <see cref="History.With"/> the single
/// way a history is constructed. It bounds how much memory an unbroken editing session can hold:
/// every event owns the inverted steps that undo it, so a paste of a large document is retained in
/// full for as long as it is reachable.
/// </para>
/// </remarks>
public readonly record struct HistoryPolicy(
    TimeSpan Window,
    bool Enabled = true,
    int Limit = HistoryPolicy.Unlimited)
{
    /// <summary>The value of <see cref="Limit"/> that means the stack is never trimmed.</summary>
    public const int Unlimited = -1;

    /// <summary>The default: half a second, and an unbounded stack.</summary>
    public static HistoryPolicy Default { get; } = new(TimeSpan.FromMilliseconds(500));

    /// <summary>Never group. Every transaction becomes its own undo step.</summary>
    public static HistoryPolicy Never { get; } = new(TimeSpan.Zero, Enabled: false);

    /// <summary>Whether a transaction should be folded into the event that precedes it.</summary>
    /// <param name="previous">The event already on the stack.</param>
    /// <param name="tr">The transaction just applied.</param>
    public bool ShouldCoalesce(HistoryEvent previous, Transaction tr)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(tr);

        if (!Enabled || previous.Kind != tr.Kind)
        {
            return false;
        }

        // Reshaping the document, and bringing content in from outside, are landmarks. A reader
        // undoing past one expects to arrive exactly there, and not somewhere inside it.
        if (tr.Kind is TransactionKind.Structure or TransactionKind.Paste)
        {
            return false;
        }

        if (tr.Time < previous.Time || tr.Time - previous.Time > Window)
        {
            return false;
        }

        // Where the previous edit left the caret is where this one has to have started.
        if (previous.SelectionAfter != tr.Before.Selection)
        {
            return false;
        }

        var (from, to) = Extent(tr);
        var (previousFrom, previousTo) = MappedExtent(previous, tr);

        return from <= previousTo && to >= previousFrom;
    }

    /// <summary>The range of positions a transaction touched, in the coordinates it ends in.</summary>
    /// <param name="tr">The transaction.</param>
    /// <remarks>
    /// A map's coordinates are those of the document as it stood when its own step ran, so in a
    /// transaction of several steps the earlier ranges have been moved since by the later ones.
    /// Comparing them as they are would mix coordinate spaces and misplace the range, which
    /// shows up as an edit that should have continued a run starting a new undo entry instead.
    /// </remarks>
    internal static (int From, int To) Extent(Transaction tr)
    {
        var from = int.MaxValue;
        var to = int.MinValue;
        var maps = tr.Mapping.Maps;

        for (var i = 0; i < maps.Length; i++)
        {
            if (maps[i].IsIdentity)
            {
                continue;
            }

            // Both ends are positions in the document as it stood after this map, so carrying
            // them through the maps that follow lands them in the transaction's final space.
            var start = tr.Mapping.MapRange(maps[i].Start, i + 1, maps.Length, Assoc.Before);
            var end = tr.Mapping.MapRange(
                maps[i].Start + maps[i].NewSize, i + 1, maps.Length, Assoc.After);

            from = Math.Min(from, start.Pos);
            to = Math.Max(to, end.Pos);
        }

        return from == int.MaxValue ? (0, 0) : (from, to);
    }

    /// <summary>A previous event's range, carried forward through a later transaction.</summary>
    /// <param name="previous">The earlier event.</param>
    /// <param name="tr">The later transaction.</param>
    /// <remarks>
    /// Without this, holding Backspace would never group. Each deletion moves the previous one's
    /// range backwards, so comparing the raw numbers finds them a character apart every time.
    /// </remarks>
    internal static (int From, int To) MappedExtent(HistoryEvent previous, Transaction tr) => (
        tr.Mapping.Map(previous.ChangedFrom, Assoc.Before),
        tr.Mapping.Map(previous.ChangedTo, Assoc.After));
}

/// <summary>
/// The undo and redo stacks.
/// </summary>
/// <remarks>
/// <para>
/// Immutable, and carried alongside the state it describes, so that a state and its history cannot
/// drift apart.
/// </para>
/// <para>
/// An ordinary edit clears the redo stack. Once the document has taken a different turn, the
/// recorded way forward describes a document that no longer exists, and replaying it would apply
/// steps to positions that have moved underneath them.
/// </para>
/// </remarks>
public sealed class History
{
    private readonly ImmutableStack<HistoryEvent> _undo;
    private readonly ImmutableStack<HistoryEvent> _redo;

    private History(
        ImmutableStack<HistoryEvent> undo,
        ImmutableStack<HistoryEvent> redo,
        int undoDepth,
        int redoDepth,
        HistoryPolicy policy)
    {
        _undo = undo;
        _redo = redo;
        UndoDepth = undoDepth;
        RedoDepth = redoDepth;
        Policy = policy;
    }

    /// <summary>An empty history with the default grouping policy.</summary>
    public static History Empty { get; } = With(HistoryPolicy.Default);

    /// <summary>How many undo steps are available.</summary>
    public int UndoDepth { get; }

    /// <summary>How many redo steps are available.</summary>
    public int RedoDepth { get; }

    /// <summary>When two adjacent edits undo as one.</summary>
    public HistoryPolicy Policy { get; }

    /// <summary>Whether there is anything to undo.</summary>
    public bool CanUndo => UndoDepth > 0;

    /// <summary>Whether there is anything to redo.</summary>
    public bool CanRedo => RedoDepth > 0;

    /// <summary>An empty history with a given grouping policy.</summary>
    /// <param name="policy">The policy.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The policy's <see cref="HistoryPolicy.Limit"/> is below <see cref="HistoryPolicy.Unlimited"/>.
    /// </exception>
    public static History With(HistoryPolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            policy.Limit, HistoryPolicy.Unlimited, nameof(policy));

        return new(
            ImmutableStack<HistoryEvent>.Empty,
            ImmutableStack<HistoryEvent>.Empty,
            0,
            0,
            policy);
    }

    /// <summary>The event that would be undone next, or null if there is none.</summary>
    public HistoryEvent? Pending => _undo.IsEmpty ? null : _undo.Peek();

    /// <summary>This history under a different policy, trimmed to it at once.</summary>
    /// <param name="policy">The new policy.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/>'s <see cref="HistoryPolicy.Limit"/> is below
    /// <see cref="HistoryPolicy.Unlimited"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Lowering a limit takes effect now rather than at the next edit. A host that has just said
    /// "keep two steps" and can still undo five has been told something untrue, and the steps it
    /// wanted dropped are exactly the ones it is holding memory for.
    /// </para>
    /// <para>
    /// Both stacks are trimmed. During ordinary editing the redo stack needs no bound of its own -
    /// it only grows by undoing, so it can never get deeper than the undo stack was - but lowering
    /// the limit under a deep redo stack is the one way that reasoning fails.
    /// </para>
    /// </remarks>
    public History WithPolicy(HistoryPolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            policy.Limit, HistoryPolicy.Unlimited, nameof(policy));

        if (policy.Limit < 0)
        {
            return new History(_undo, _redo, UndoDepth, RedoDepth, policy);
        }

        return new History(
            Trimmed(_undo, Math.Min(UndoDepth, policy.Limit)),
            Trimmed(_redo, Math.Min(RedoDepth, policy.Limit)),
            Math.Min(UndoDepth, policy.Limit),
            Math.Min(RedoDepth, policy.Limit),
            policy);
    }

    /// <summary>This history with a transaction recorded in it.</summary>
    /// <param name="tr">The transaction that was applied.</param>
    /// <remarks>
    /// <para>
    /// A transaction that changed nothing is not recorded. Moving the caret is not an edit, and
    /// treating it as one would make Ctrl+Z sometimes appear to do nothing at all.
    /// </para>
    /// <para>
    /// Neither is one marked <see cref="TransactionKind.History"/>. An application is expected to
    /// funnel every transaction it applies through here; <see cref="Undo"/> and <see cref="Redo"/>
    /// already move the event between the stacks themselves, and recording their work again would
    /// make Ctrl+Z undo itself.
    /// </para>
    /// </remarks>
    public History Record(Transaction tr)
    {
        ArgumentNullException.ThrowIfNull(tr);

        tr.Seal();

        if (!tr.ChangedDoc || tr.Kind == TransactionKind.History)
        {
            return this;
        }

        var (from, to) = HistoryPolicy.Extent(tr);

        if (!_undo.IsEmpty && Policy.ShouldCoalesce(_undo.Peek(), tr))
        {
            var previous = _undo.Peek();
            var (previousFrom, previousTo) = HistoryPolicy.MappedExtent(previous, tr);

            var merged = previous with
            {
                // The new steps undo first, because they happened last.
                Undo = [.. tr.Invert(), .. previous.Undo],
                SelectionAfter = tr.SelectionAfter,
                Time = tr.Time,
                ChangedFrom = Math.Min(previousFrom, from),
                ChangedTo = Math.Max(previousTo, to),
            };

            return new History(
                _undo.Pop().Push(merged),
                ImmutableStack<HistoryEvent>.Empty,
                UndoDepth,
                0,
                Policy);
        }

        var recorded = new HistoryEvent(
            tr.Invert(),
            tr.Before.Selection,
            tr.SelectionAfter,
            tr.Kind,
            tr.Time,
            from,
            to);

        if (Policy.Limit == 0)
        {
            // Nothing is kept, but the redo stack still goes: the document has moved on, and a
            // redo recorded before it moved describes a document that no longer exists.
            return new History(
                ImmutableStack<HistoryEvent>.Empty,
                ImmutableStack<HistoryEvent>.Empty,
                0,
                0,
                Policy);
        }

        var undo = _undo.Push(recorded);
        var depth = UndoDepth + 1;

        if (Policy.Limit > 0 && depth > Policy.Limit)
        {
            undo = Trimmed(undo, Policy.Limit);
            depth = Policy.Limit;
        }

        return new History(
            undo,
            ImmutableStack<HistoryEvent>.Empty,
            depth,
            0,
            Policy);
    }

    /// <summary>The top <paramref name="keep"/> events of a stack, oldest dropped.</summary>
    /// <remarks>
    /// <para>
    /// A stack cannot drop its bottom, so this rebuilds. That is O(<paramref name="keep"/>), and it
    /// runs only on a record that crosses the limit, so the stack is at most one over and exactly
    /// one event is discarded each time.
    /// </para>
    /// <para>
    /// Measured (Release, median of 7 runs of 200 records each, a 400-character paragraph): a
    /// record that does not trim costs 1.6 us; at a limit of 10 a trimming record costs 3.8 us, at
    /// 100 it costs 8.3 us, and at 1000 it costs 38.7 us. So the trim is roughly 6.6 us at the
    /// hundred-step limit a host is likely to pick, against milliseconds of layout for the
    /// keystroke that caused it. A very large limit is the case worth knowing about, and it is the
    /// case where a host has already chosen to hold that much memory.
    /// </para>
    /// <para>
    /// Trimming happens on record and never on undo. Walking back down a trimmed stack has to
    /// leave the events it passes on the redo side, or undoing to the bottom and redoing would
    /// not return to where it started.
    /// </para>
    /// </remarks>
    private static ImmutableStack<HistoryEvent> Trimmed(ImmutableStack<HistoryEvent> undo, int keep)
    {
        var newest = new HistoryEvent[keep];
        var rest = undo;

        for (var i = 0; i < keep; i++)
        {
            newest[i] = rest.Peek();
            rest = rest.Pop();
        }

        var trimmed = ImmutableStack<HistoryEvent>.Empty;

        // Back on in reverse, so that the newest ends up on top again.
        for (var i = keep - 1; i >= 0; i--)
        {
            trimmed = trimmed.Push(newest[i]);
        }

        return trimmed;
    }

    /// <summary>Undoes the most recent event, or does nothing if there is none.</summary>
    /// <param name="state">The current state.</param>
    /// <returns>The state as it was, and the history with that event moved to the redo stack.</returns>
    public (EditorState State, History History) Undo(EditorState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (_undo.IsEmpty)
        {
            return (state, this);
        }

        var top = _undo.Peek();
        var (undone, inverse) = Replay(state, top);

        return (
            undone,
            new History(
                _undo.Pop(),
                _redo.Push(Reversed(top, inverse)),
                UndoDepth - 1,
                RedoDepth + 1,
                Policy));
    }

    /// <summary>Redoes the most recently undone event, or does nothing if there is none.</summary>
    /// <param name="state">The current state.</param>
    /// <returns>The state as it was, and the history with that event back on the undo stack.</returns>
    public (EditorState State, History History) Redo(EditorState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (_redo.IsEmpty)
        {
            return (state, this);
        }

        var top = _redo.Peek();
        var (redone, inverse) = Replay(state, top);

        return (
            redone,
            new History(
                _undo.Push(Reversed(top, inverse)),
                _redo.Pop(),
                UndoDepth + 1,
                RedoDepth - 1,
                Policy));
    }

    private static HistoryEvent Reversed(HistoryEvent replayed, ImmutableArray<Step> inverse) =>
        replayed with
        {
            Undo = inverse,
            SelectionBefore = replayed.SelectionAfter,
            SelectionAfter = replayed.SelectionBefore,
        };

    private static (EditorState State, ImmutableArray<Step> Inverse) Replay(
        EditorState state,
        HistoryEvent replayed)
    {
        var tr = state.Transaction().As(TransactionKind.History).At(replayed.Time);

        foreach (var step in replayed.Undo)
        {
            tr.Step(step);
        }

        if (!tr.Failures.IsEmpty)
        {
            // Not a user error. A recorded inverse that will not apply to the document it was
            // recorded against means a step lied about its own inverse, and carrying on would
            // leave the reader with a document nobody asked for.
            throw new InvalidOperationException(
                "History could not be replayed: " + string.Join("; ", tr.Failures));
        }

        // The selection is restored rather than mapped forward: undo should put the reader back
        // where they were working, and that is information the steps themselves do not carry.
        // Mapping it through nothing is how it gets checked against the document it lands in.
        tr.SetSelection(replayed.SelectionBefore.Map(tr.Doc, Mapping.Empty));

        return (state.Apply(tr), tr.Invert());
    }
}
