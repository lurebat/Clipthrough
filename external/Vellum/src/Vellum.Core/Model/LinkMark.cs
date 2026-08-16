namespace Vellum;

/// <summary>
/// A hyperlink attached to a run of text.
/// </summary>
/// <remarks>
/// A reference type rather than a struct because links are rare relative to the number of
/// mark sets, and <see cref="MarkSet"/> stays small by holding a reference. Equality is
/// structural, which is what keeps mark-span merging correct for adjacent identical links.
/// </remarks>
public sealed record LinkMark
{
    /// <summary>Creates a link.</summary>
    /// <param name="href">The target. Must not be blank.</param>
    /// <param name="title">Optional tooltip text. Blank is treated as absent.</param>
    /// <exception cref="ArgumentException"><paramref name="href"/> is null, empty or whitespace.</exception>
    public LinkMark(string href, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(href);

        Href = href;
        Title = string.IsNullOrWhiteSpace(title) ? null : title;
    }

    /// <summary>The link target, verbatim as authored or imported.</summary>
    /// <remarks>
    /// Deliberately not sanitized here. Refusing dangerous schemes is the importer's job
    /// (architecture §7), and doing it in the model too would mean a document could not
    /// round-trip through its own serializer.
    /// </remarks>
    public string Href { get; }

    /// <summary>Optional tooltip text, or null.</summary>
    public string? Title { get; }

    /// <summary>Formats as the href, plus the title when there is one.</summary>
    public override string ToString() => Title is null ? Href : $"{Href} \"{Title}\"";
}
