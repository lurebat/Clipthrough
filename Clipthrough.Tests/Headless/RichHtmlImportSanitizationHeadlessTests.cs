using Avalonia.Headless.XUnit;
using VellumText;
using VellumText.Interop.Html;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Script and style bodies must not surface as visible text in the rendered
/// preview. Clipboard HTML arrives from arbitrary applications and web pages,
/// and the rendered pane imports it through <c>HtmlFormat.Instance.Import</c> -
/// so that importer's raw-text handling is a boundary this application depends
/// on, not an upstream detail.
/// </summary>
/// <remarks>
/// <strong>The guarantor is VellumText, not Clipthrough.</strong>
/// <c>RichDocumentView.Import</c> delegates straight to the library; there is no
/// sanitising step of our own on this path, so nothing here would fail if we
/// removed code. That is deliberate and worth stating, because a reader finding
/// these tests could otherwise credit us for the library's work.
///
/// They exist anyway, for a reason the dependency itself demonstrated: the leak
/// was fixed in VellumText 0.5.0's HTML importer and then reappeared in 0.7.0's
/// new Markdown importer, through a different entry point reaching the same
/// model, because the test covering it handled only the block form. A property
/// our users need, enforced entirely by someone else's code, with no local
/// evidence, is exactly the thing a version bump can take away silently - and
/// the suite would stay green.
///
/// If these ever fail, the fault is upstream and the fix is not here: pin the
/// previous version and report it.
/// </remarks>
public sealed class RichHtmlImportSanitizationHeadlessTests
{
    private static string VisibleText(string html)
        => DocumentText.Of(HtmlFormat.Instance.Import(html).Doc);

    [AvaloniaTheory]
    [InlineData("<html><body><p>before</p><script>stealTheClipboard()</script><p>after</p></body></html>", "stealTheClipboard")]
    [InlineData("<html><body><p>before</p><style>.x{color:red}</style><p>after</p></body></html>", ".x{color:red}")]
    public void AScriptOrStyleBody_DoesNotSurfaceAsVisibleText(string html, string forbidden)
    {
        var text = VisibleText(html);

        Assert.DoesNotContain(forbidden, text, System.StringComparison.Ordinal);

        // The premise: the surrounding document really did import, so a parser
        // that silently returned nothing cannot be what satisfied the assertion.
        Assert.Contains("before", text, System.StringComparison.Ordinal);
        Assert.Contains("after", text, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The inline and unterminated forms, which are how the regression upstream
    /// got past a test that covered the block form only.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("<html><body>a <b>x<script>evil()</script>y</b> b</body></html>", "evil()")]
    [InlineData("<html><body><p>kept</p><script>trailing()", "trailing()")]
    public void AScriptInAnAwkwardPosition_StillDoesNotSurface(string html, string forbidden)
    {
        Assert.DoesNotContain(forbidden, VisibleText(html), System.StringComparison.Ordinal);
    }
}
