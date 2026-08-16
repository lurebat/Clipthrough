namespace Vellum.Interop.Html;

/// <summary>
/// How much of the incoming HTML to trust.
/// </summary>
/// <remarks>
/// The defaults are the paranoid ones. HTML arriving on the clipboard or from a file is hostile
/// input in the ordinary case, never mind the malicious one, so anything that could reach the
/// network or execute is off unless the host turns it on deliberately.
/// </remarks>
public sealed record HtmlImportOptions
{
    /// <summary>The defaults: trust nothing that could phone home.</summary>
    public static HtmlImportOptions Default { get; } = new();

    /// <summary>
    /// Whether to keep images whose source is a remote URL. Off by default.
    /// </summary>
    /// <remarks>
    /// A remote image is a tracking pixel with extra steps: rendering one tells whoever sent the
    /// document that it was opened, from which address, and when. Hosts that already accept that
    /// tradeoff — a mail client showing a message the user asked to see — can turn it on.
    /// </remarks>
    public bool AllowRemoteImages { get; init; }

    /// <summary>
    /// Whether to keep images encoded inline as a <c>data:</c> URL. On by default.
    /// </summary>
    /// <remarks>
    /// This is how a pasted image actually arrives — from RTF, from a browser, from Word — so
    /// turning it off means images do not survive a paste at all. It is materially safer than a
    /// remote image because it reaches no network, and only URLs whose media type is an image are
    /// accepted regardless.
    /// </remarks>
    public bool AllowDataImages { get; init; } = true;

    /// <summary>
    /// The largest number of bytes an inline <c>data:</c> image may take, before decoding.
    /// Defaults to 8 MiB.
    /// </summary>
    /// <remarks>
    /// A bound is needed because the source controls the length: a clipboard fragment can name a
    /// hundred-megabyte image as easily as a small one, and a paste is not a good moment to find
    /// that out.
    /// </remarks>
    public int MaxDataImageBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// The deepest the element tree may nest before the importer stops descending. Defaults to 256.
    /// </summary>
    /// <remarks>
    /// The parser handles arbitrarily deep documents, but a recursive walk over one does not — a
    /// fragment of ten thousand nested <c>&lt;div&gt;</c> is a stack overflow, and a stack overflow
    /// cannot be caught. Content below the limit is kept as text.
    /// </remarks>
    public int MaxDepth { get; init; } = 256;

    /// <summary>
    /// The address relative URLs are resolved against, or null to drop relative URLs.
    /// </summary>
    /// <remarks>
    /// Null is the safe default: a relative link in a pasted fragment has no meaning in the
    /// document it is being pasted into, and guessing one is how a link ends up pointing somewhere
    /// nobody intended.
    /// </remarks>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Whether to keep the <c>class</c> and <c>id</c> attributes. Off by default, and they are
    /// dropped rather than reported, since the document model has nowhere to put them.
    /// </summary>
    public bool PreserveIdentifiers { get; init; }
}
