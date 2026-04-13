using System.Text;

namespace Clipthrough.Presentation;

/// <summary>
/// Converts an RTF string to HTML using RtfPipe.
/// </summary>
public static class RtfToHtmlConverter
{
    static RtfToHtmlConverter()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Convert(string rtf)
    {
        return RtfPipe.Rtf.ToHtml(rtf);
    }
}
