using System;
using System.Globalization;
using System.Net;
using System.Text;
using Avalonia.Media;
using RtfDomParserAv;

namespace Clipthrough.Presentation;

/// <summary>
/// Converts an RTF string to HTML with inline styles for color-accurate rendering
/// in AvRichTextBox via <c>LoadHtml()</c>. This bypasses the FlowDocument RTF path
/// which does not reliably render foreground colors.
/// </summary>
public static class RtfToHtmlConverter
{
    private static readonly Color s_defaultForeground = Colors.Black;
    private static readonly Color s_transparent = Colors.Transparent;

    public static string Convert(string rtf)
    {
        var doc = new RTFDomDocument();
        doc.LoadRTFText(rtf);

        var sb = new StringBuilder(rtf.Length);
        sb.Append("<html><body>");

        var hasParagraphs = false;
        foreach (RTFDomElement element in doc.Elements)
        {
            switch (element)
            {
                case RTFDomParagraph paragraph:
                    AppendParagraph(sb, paragraph);
                    hasParagraphs = true;
                    break;

                case RTFDomTable table:
                    AppendTable(sb, table, doc.ColorTable);
                    hasParagraphs = true;
                    break;
            }
        }

        // Handle root-level inlines (RTF without explicit \par)
        if (!hasParagraphs)
        {
            sb.Append("<p>");
            AppendInlineElements(sb, doc.Elements);
            sb.Append("</p>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void AppendParagraph(StringBuilder sb, RTFDomParagraph paragraph)
    {
        sb.Append("<p");
        AppendParagraphStyle(sb, paragraph);
        sb.Append('>');

        if (paragraph.Elements.Count == 0)
        {
            sb.Append("&nbsp;");
        }
        else
        {
            AppendInlineElements(sb, paragraph.Elements);
        }

        sb.Append("</p>");
    }

    private static void AppendParagraphStyle(StringBuilder sb, RTFDomParagraph paragraph)
    {
        var hasStyle = false;

        var align = paragraph.Format.Align switch
        {
            RTFAlignment.Center => "center",
            RTFAlignment.Right => "right",
            RTFAlignment.Justify => "justify",
            _ => null,
        };

        if (align is not null)
        {
            sb.Append(hasStyle ? "" : " style=\"");
            hasStyle = true;
            sb.Append("text-align:").Append(align).Append(';');
        }

        if (!IsDefaultBackground(paragraph.Format.BackColor))
        {
            sb.Append(hasStyle ? "" : " style=\"");
            hasStyle = true;
            sb.Append("background-color:").Append(ColorToHex(paragraph.Format.BackColor)).Append(';');
        }

        if (!string.IsNullOrWhiteSpace(paragraph.Format.FontName))
        {
            sb.Append(hasStyle ? "" : " style=\"");
            hasStyle = true;
            sb.Append("font-family:").Append(WebUtility.HtmlEncode(paragraph.Format.FontName)).Append(';');
        }

        if (hasStyle)
        {
            sb.Append('"');
        }
    }

    private static void AppendInlineElements(StringBuilder sb, RTFDomElementList elements)
    {
        foreach (RTFDomElement element in elements)
        {
            switch (element)
            {
                case RTFDomText text:
                    AppendTextRun(sb, text);
                    break;

                case RTFDomLineBreak:
                    sb.Append("<br/>");
                    break;

                case RTFDomImage image:
                    AppendImage(sb, image);
                    break;

                case RTFDomField field:
                    AppendField(sb, field);
                    break;

                default:
                    // Unknown element — skip silently
                    break;
            }
        }
    }

    private static void AppendTextRun(StringBuilder sb, RTFDomText text)
    {
        var content = text.Text;
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        sb.Append("<span");
        AppendRunStyle(sb, text.Format);
        sb.Append('>');
        sb.Append(WebUtility.HtmlEncode(content));
        sb.Append("</span>");
    }

    private static void AppendRunStyle(StringBuilder sb, DocumentFormatInfo format)
    {
        sb.Append(" style=\"");

        if (!IsDefaultForeground(format.TextColor))
        {
            sb.Append("color:").Append(ColorToHex(format.TextColor)).Append(';');
        }

        if (!IsDefaultBackground(format.BackColor))
        {
            sb.Append("background-color:").Append(ColorToHex(format.BackColor)).Append(';');
        }

        if (format.Bold)
        {
            sb.Append("font-weight:bold;");
        }

        if (format.Italic)
        {
            sb.Append("font-style:italic;");
        }

        if (format.Underline)
        {
            sb.Append("text-decoration:underline;");
        }
        else if (format.Strikeout)
        {
            sb.Append("text-decoration:line-through;");
        }

        if (format.FontSize > 0)
        {
            sb.Append("font-size:")
              .Append(format.FontSize.ToString(CultureInfo.InvariantCulture))
              .Append("pt;");
        }

        if (!string.IsNullOrWhiteSpace(format.FontName))
        {
            sb.Append("font-family:").Append(WebUtility.HtmlEncode(format.FontName)).Append(';');
        }

        if (format.Superscript)
        {
            sb.Append("vertical-align:super;font-size:smaller;");
        }
        else if (format.Subscript)
        {
            sb.Append("vertical-align:sub;font-size:smaller;");
        }

        sb.Append('"');
    }

    private static void AppendImage(StringBuilder sb, RTFDomImage image)
    {
        if (image.Data is not { Length: > 0 })
        {
            return;
        }

        var base64 = System.Convert.ToBase64String(image.Data);
        var width = Math.Max(1, (int)TwipToPixels(image.Width));
        var height = Math.Max(1, (int)TwipToPixels(image.Height));

        sb.Append("<img src=\"data:image/png;base64,")
          .Append(base64)
          .Append("\" width=\"")
          .Append(width)
          .Append("\" height=\"")
          .Append(height)
          .Append("\" />");
    }

    private static void AppendField(StringBuilder sb, RTFDomField field)
    {
        var result = field.Result;
        if (result is null)
        {
            return;
        }

        foreach (RTFDomElement element in result.Elements)
        {
            if (element is RTFDomText fieldText)
            {
                AppendTextRun(sb, fieldText);
            }
        }
    }

    private static void AppendTable(StringBuilder sb, RTFDomTable table, RTFColorTable colorTable)
    {
        sb.Append("<table style=\"border-collapse:collapse;\">");

        foreach (RTFDomElement rowElement in table.Elements)
        {
            if (rowElement is not RTFDomTableRow row)
            {
                continue;
            }

            sb.Append("<tr>");

            foreach (RTFDomElement cellElement in row.Elements)
            {
                if (cellElement is not RTFDomTableCell cell)
                {
                    continue;
                }

                sb.Append("<td style=\"border:1px solid #ccc;padding:4px;");

                foreach (RTFAttribute attr in cell.Attributes)
                {
                    if (string.Equals(attr.Name, "clcbpat", StringComparison.OrdinalIgnoreCase))
                    {
                        var bgColor = colorTable.GetColor(attr.Value, Colors.Transparent);
                        if (!IsDefaultBackground(bgColor))
                        {
                            sb.Append("background-color:").Append(ColorToHex(bgColor)).Append(';');
                        }
                    }
                }

                sb.Append("\">");

                foreach (RTFDomElement parElement in cell.Elements)
                {
                    if (parElement is RTFDomParagraph cellPar)
                    {
                        AppendParagraph(sb, cellPar);
                    }
                }

                sb.Append("</td>");
            }

            sb.Append("</tr>");
        }

        sb.Append("</table>");
    }

    private static bool IsDefaultForeground(Color color)
    {
        return color == s_defaultForeground
            || color == s_transparent
            || color.A == 0;
    }

    private static bool IsDefaultBackground(Color color)
    {
        return color == s_transparent
            || color.A == 0;
    }

    private static string ColorToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static double TwipToPixels(int twips)
    {
        return twips / 1440.0 * 96.0;
    }
}
