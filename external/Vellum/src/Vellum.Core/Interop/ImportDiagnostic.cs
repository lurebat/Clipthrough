namespace Vellum;

/// <summary>How badly an importer had to compromise.</summary>
public enum DiagnosticSeverity
{
    /// <summary>
    /// Something in the source could not be represented and the nearest supported thing was used
    /// instead. The document is sound; it is just not exactly what was asked for.
    /// </summary>
    Downgraded = 0,

    /// <summary>
    /// Something was removed rather than downgraded, because it is not safe to keep: script,
    /// event handlers, embedded objects, dangerous URLs.
    /// </summary>
    Dropped,

    /// <summary>
    /// The source is malformed. The importer recovered and carried on — importers do not throw —
    /// but the result may be missing whatever the malformed region contained.
    /// </summary>
    Malformed,
}

/// <summary>
/// One thing an importer could not do faithfully.
/// </summary>
/// <remarks>
/// Import is the one part of the editor that sees input nobody wrote for it: a clipboard fragment
/// from Word, a file from 1998, a document from a generator that has never been tested against
/// anything. So an importer never throws into the UI and never silently discards. It produces the
/// best document it can and says, item by item, where it gave ground. A host that does not care
/// ignores the list; a host that does can show it.
/// </remarks>
/// <param name="Severity">How badly the importer had to compromise.</param>
/// <param name="Message">What happened, in terms a user of the host application could act on.</param>
/// <param name="Context">
/// The construct it happened to — an element name, a control word — or null when there is nothing
/// useful to name.
/// </param>
public readonly record struct ImportDiagnostic(
    DiagnosticSeverity Severity,
    string Message,
    string? Context = null)
{
    /// <inheritdoc/>
    public override string ToString() =>
        Context is null ? $"{Severity}: {Message}" : $"{Severity}: {Message} ({Context})";
}
