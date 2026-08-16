using System.Collections.Immutable;

namespace Vellum;

/// <summary>
/// What an importer produced, together with everything it had to compromise on.
/// </summary>
/// <remarks>
/// There is no failed variant. An importer that cannot make sense of its input returns an empty
/// document and a <see cref="DiagnosticSeverity.Malformed"/> diagnostic, because the caller is
/// usually a paste and a paste that throws is worse than a paste that pastes nothing. Callers who
/// want to treat that as failure can ask <see cref="IsEmpty"/>.
/// </remarks>
public sealed class ImportResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="doc">The imported document.</param>
    /// <param name="diagnostics">What the importer had to compromise on, or null for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
    public ImportResult(DocumentNode doc, IEnumerable<ImportDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(doc);

        Doc = doc;
        Diagnostics = diagnostics?.ToImmutableArray() ?? [];
    }

    /// <summary>The imported document.</summary>
    public DocumentNode Doc { get; }

    /// <summary>Everything the importer could not do faithfully, in the order it happened.</summary>
    public ImmutableArray<ImportDiagnostic> Diagnostics { get; }

    /// <summary>Whether the import produced no content at all.</summary>
    /// <remarks>
    /// A document always has at least one block, so "empty" means one empty paragraph rather than
    /// no blocks. This is the question a caller asks before deciding a paste did nothing.
    /// </remarks>
    public bool IsEmpty =>
        Doc.Blocks.Length == 0
        || (Doc.Blocks.Length == 1
            && Doc.Blocks[0] is ParagraphNode { Content.Length: 0 });

    /// <summary>Whether anything was dropped for safety rather than merely downgraded.</summary>
    public bool AnythingDropped =>
        Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Dropped);

    /// <summary>The document as a slice, ready to be pasted into an existing document.</summary>
    /// <remarks>
    /// Open at neither end: an imported document is a run of whole blocks, so pasting it into the
    /// middle of a paragraph splits that paragraph rather than merging into it. A caller that
    /// wants the single-paragraph case to merge should check for it and use the content directly.
    /// </remarks>
    public Slice ToSlice() => new(Doc.Blocks, 0, 0);
}
