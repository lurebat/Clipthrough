namespace Vellum;

/// <summary>
/// What happened when a step was applied.
/// </summary>
/// <remarks>
/// A step that would produce a document the schema rejects fails instead. The editor drops the
/// transaction and logs it; it never applies a partially valid tree, because a tree that
/// violates its own schema will fail somewhere further away, in the layout or the exporter,
/// where the cause is no longer visible.
/// </remarks>
public readonly record struct StepResult
{
    private StepResult(DocumentNode? doc, string? failure)
    {
        Doc = doc;
        Failure = failure;
    }

    /// <summary>The resulting document, or null if the step failed.</summary>
    public DocumentNode? Doc { get; }

    /// <summary>Why the step failed, or null if it did not.</summary>
    public string? Failure { get; }

    /// <summary>Whether the step succeeded.</summary>
    public bool IsOk => Doc is not null;

    /// <summary>A successful result.</summary>
    /// <param name="doc">The resulting document.</param>
    public static StepResult Ok(DocumentNode doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        return new StepResult(doc, null);
    }

    /// <summary>A failed result.</summary>
    /// <param name="reason">Why the step could not be applied.</param>
    public static StepResult Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new StepResult(null, reason);
    }

    /// <inheritdoc/>
    public override string ToString() => IsOk ? "ok" : $"failed: {Failure}";
}

/// <summary>
/// One atomic, invertible, mappable change to a document.
/// </summary>
/// <remarks>
/// <para>
/// Nothing mutates a document directly. Everything - typing, paste, table operations - becomes
/// a sequence of steps, and that single constraint is what buys undo, incremental view
/// invalidation, and the rebase primitive collaboration would need.
/// </para>
/// <para>
/// The four obligations are deliberately all on one type. A change that can be applied but not
/// inverted breaks undo; one that cannot report how it moved positions leaves every held
/// position stale; one that cannot be rebased forecloses collaboration. Requiring all four up
/// front is what stops a change from being added that quietly cannot do one of them.
/// </para>
/// </remarks>
public abstract record Step
{
    /// <summary>Applies this step to a document.</summary>
    /// <param name="doc">The document to change.</param>
    public abstract StepResult Apply(DocumentNode doc);

    /// <summary>
    /// The step that undoes this one, given the document as it was beforehand.
    /// </summary>
    /// <param name="docBefore">The document this step was applied to.</param>
    /// <remarks>
    /// The prior document is needed because a step records what to do, not what it displaced.
    /// Undo is therefore cheap in memory - no snapshots - at the cost of needing the old
    /// document in hand at the moment the inverse is built, which is exactly when history has it.
    /// </remarks>
    public abstract Step Invert(DocumentNode docBefore);

    /// <summary>How this step moves positions.</summary>
    /// <remarks>
    /// Answerable without applying the step, which is what lets a transaction build its whole
    /// mapping up front.
    /// </remarks>
    public abstract StepMap GetMap();

    /// <summary>
    /// This step rewritten to apply to a document that other steps have already changed.
    /// </summary>
    /// <param name="mapping">The changes this step has not seen.</param>
    /// <returns>The rebased step, or null if it no longer has anything to act on.</returns>
    public abstract Step? Map(Mapping mapping);
}
