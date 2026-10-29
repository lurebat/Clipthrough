using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Platform;

namespace Clipthrough.Tests;

/// <summary>
/// A rich clip as a copy from a browser stores it: the page's HTML as CF_HTML in
/// the bytes, and the plain text the browser offers alongside it as the text.
/// The markup is the page copied from Edge in live check C3 (2026-10-29).
/// </summary>
internal static class RichBrowserClip
{
    public const string Fragment =
        "<h1>C3 Heading</h1>\n"
        + "<p>Para with <b>bold</b>, <i>italic</i> and a <a href=\"https://example.com/c7-link\">c7 link</a>.</p>\n"
        + "<ul><li>first item</li><li>second item</li></ul>";

    public const string PlainText = "C3 Heading\r\nPara with bold, italic and a c7 link.\r\nfirst item\r\nsecond item";

    public static Task<ClipEntry?> CaptureAsync(IClipStoreService store)
        => store.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = Encoding.UTF8.GetBytes(RichClipboardFormatting.BuildCfHtml(Fragment)),
            ContentText = PlainText,
            ContentType = ContentType.RichText,
            ContentFormat = ClipContentFormat.Html,
            SourceApp = "msedge.exe",
        });
}
