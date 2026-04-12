using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace Clipthrough.Presentation;

/// <summary>
/// Converts arbitrary clipboard HTML into the subset AvRichTextBox can render:
/// <c>&lt;body&gt;</c> containing <c>&lt;p&gt;</c> and <c>&lt;table&gt;</c> blocks,
/// where each <c>&lt;p&gt;</c> contains only <c>&lt;span&gt;</c>, <c>&lt;br&gt;</c>,
/// <c>&lt;img&gt;</c>, or text nodes.
///
/// All other tags are flattened: block-level elements become <c>&lt;p&gt;</c>,
/// inline elements become <c>&lt;span&gt;</c>, and styles are inherited downward.
/// </summary>
public static class HtmlFlattener
{
    private static readonly HashSet<string> s_blockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "main", "header", "footer", "nav", "aside",
        "blockquote", "figure", "figcaption", "details", "summary",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "pre", "li", "dt", "dd",
        "address", "fieldset", "legend", "dialog",
        "button", "form",
    };

    private static readonly HashSet<string> s_nativeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "span", "br", "img",
    };

    private static readonly HashSet<string> s_skipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "script", "head", "title", "meta", "link", "noscript",
    };

    private static readonly Dictionary<string, string> s_semanticStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["b"] = "font-weight:bold",
        ["strong"] = "font-weight:bold",
        ["i"] = "font-style:italic",
        ["em"] = "font-style:italic",
        ["u"] = "text-decoration:underline",
        ["s"] = "text-decoration:line-through",
        ["del"] = "text-decoration:line-through",
        ["strike"] = "text-decoration:line-through",
        ["code"] = "font-family:monospace",
        ["kbd"] = "font-family:monospace",
        ["samp"] = "font-family:monospace",
        ["var"] = "font-style:italic",
        ["mark"] = "background-color:yellow",
        ["sub"] = "vertical-align:sub;font-size:smaller",
        ["sup"] = "vertical-align:super;font-size:smaller",
        ["small"] = "font-size:smaller",
    };

    private static readonly string[] s_inheritableProps =
        ["color", "font-family", "font-size", "font-weight", "font-style", "background-color"];

    /// <summary>
    /// Flattens arbitrary HTML into AvRichTextBox-compatible structure.
    /// </summary>
    public static string Flatten(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var body = doc.DocumentNode.SelectSingleNode("//body")
                   ?? doc.DocumentNode;

        var sb = new StringBuilder(html.Length);
        sb.Append("<body>");
        FlattenChildren(body, sb, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        sb.Append("</body>");

        return sb.ToString();
    }

    private static void FlattenChildren(
        HtmlNode parent, StringBuilder sb, Dictionary<string, string> inheritedStyles)
    {
        // Check if this parent has any block-level children
        var hasBlockChildren = parent.ChildNodes.Any(c =>
            c.NodeType == HtmlNodeType.Element && s_blockTags.Contains(c.Name));

        if (hasBlockChildren)
        {
            // Process each child: block elements become <p>, inline content between them
            // is collected into an implicit <p>
            var inlineBuffer = new StringBuilder();
            foreach (var child in parent.ChildNodes)
            {
                if (child.NodeType == HtmlNodeType.Element && s_blockTags.Contains(child.Name))
                {
                    FlushInlineBuffer(inlineBuffer, sb);
                    EmitBlock(child, sb, inheritedStyles);
                }
                else
                {
                    CollectInlineContent(child, inlineBuffer, inheritedStyles);
                }
            }
            FlushInlineBuffer(inlineBuffer, sb);
        }
        else
        {
            // All inline content — emit a single <p>
            var inlineBuffer = new StringBuilder();
            foreach (var child in parent.ChildNodes)
            {
                CollectInlineContent(child, inlineBuffer, inheritedStyles);
            }

            if (inlineBuffer.Length > 0)
            {
                sb.Append("<p>").Append(inlineBuffer).Append("</p>");
            }
        }
    }

    private static void EmitBlock(
        HtmlNode node, StringBuilder sb, Dictionary<string, string> inheritedStyles)
    {
        var mergedStyles = MergeStyles(inheritedStyles, node);

        // Check if this block has nested block children
        var hasNestedBlocks = node.ChildNodes.Any(c =>
            c.NodeType == HtmlNodeType.Element && s_blockTags.Contains(c.Name));

        if (hasNestedBlocks)
        {
            // Recursively flatten nested blocks
            FlattenChildren(node, sb, mergedStyles);
        }
        else
        {
            // Leaf block — emit as <p> with inline content
            var inlineBuffer = new StringBuilder();
            foreach (var child in node.ChildNodes)
            {
                CollectInlineContent(child, inlineBuffer, mergedStyles);
            }

            if (inlineBuffer.Length > 0)
            {
                sb.Append("<p>").Append(inlineBuffer).Append("</p>");
            }
        }
    }

    private static void CollectInlineContent(
        HtmlNode node, StringBuilder buffer, Dictionary<string, string> inheritedStyles)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = WebUtility.HtmlDecode(node.InnerText);
            if (!string.IsNullOrEmpty(text))
            {
                if (inheritedStyles.Count > 0)
                {
                    buffer.Append("<span style=\"");
                    AppendStyleString(buffer, inheritedStyles);
                    buffer.Append("\">");
                    buffer.Append(WebUtility.HtmlEncode(text));
                    buffer.Append("</span>");
                }
                else
                {
                    buffer.Append("<span>").Append(WebUtility.HtmlEncode(text)).Append("</span>");
                }
            }
            return;
        }

        if (node.NodeType != HtmlNodeType.Element)
        {
            return;
        }

        if (s_skipTags.Contains(node.Name))
        {
            return;
        }

        if (node.Name.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            buffer.Append("<br/>");
            return;
        }

        if (node.Name.Equals("img", StringComparison.OrdinalIgnoreCase))
        {
            var src = node.GetAttributeValue("src", "");
            if (src.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                buffer.Append(node.OuterHtml);
            }
            return;
        }

        // For block-level elements found in inline context, just recurse into children
        // (they shouldn't be here but clipboard HTML is messy)
        var mergedStyles = MergeStyles(inheritedStyles, node);

        if (node.Name.Equals("span", StringComparison.OrdinalIgnoreCase))
        {
            // Preserve span as-is but merge inherited styles for bare text children
            var style = node.GetAttributeValue("style", "");
            var spanStyles = MergeInheritedIntoExisting(inheritedStyles, style);

            if (node.ChildNodes.Count == 1 && node.ChildNodes[0].NodeType == HtmlNodeType.Text)
            {
                // Simple span with text — emit directly with merged styles
                var text = WebUtility.HtmlDecode(node.InnerText);
                if (!string.IsNullOrEmpty(text))
                {
                    buffer.Append("<span");
                    if (!string.IsNullOrEmpty(spanStyles))
                    {
                        buffer.Append(" style=\"").Append(spanStyles).Append('"');
                    }
                    buffer.Append('>').Append(WebUtility.HtmlEncode(text)).Append("</span>");
                }
            }
            else
            {
                // Span with mixed children — recurse
                foreach (var child in node.ChildNodes)
                {
                    CollectInlineContent(child, buffer, mergedStyles);
                }
            }
            return;
        }

        // Any other element: recurse into children with merged styles
        foreach (var child in node.ChildNodes)
        {
            CollectInlineContent(child, buffer, mergedStyles);
        }
    }

    private static void FlushInlineBuffer(StringBuilder inlineBuffer, StringBuilder sb)
    {
        if (inlineBuffer.Length > 0)
        {
            sb.Append("<p>").Append(inlineBuffer).Append("</p>");
            inlineBuffer.Clear();
        }
    }

    private static Dictionary<string, string> MergeStyles(
        Dictionary<string, string> inherited, HtmlNode node)
    {
        var merged = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);

        // Add semantic styles (e.g. <b> → font-weight:bold)
        if (s_semanticStyles.TryGetValue(node.Name, out var semantic))
        {
            foreach (var prop in semantic.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIdx = prop.IndexOf(':');
                if (colonIdx > 0)
                {
                    merged[prop[..colonIdx].Trim()] = prop[(colonIdx + 1)..].Trim();
                }
            }
        }

        // Parse inline style attribute — these take precedence
        var style = node.GetAttributeValue("style", "");
        if (string.IsNullOrWhiteSpace(style))
        {
            return merged;
        }

        foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIdx = part.IndexOf(':');
            if (colonIdx <= 0)
            {
                continue;
            }

            var key = part[..colonIdx].Trim();
            var value = part[(colonIdx + 1)..].Trim();

            // Only inherit properties that make sense to propagate
            if (IsInheritable(key))
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    private static string MergeInheritedIntoExisting(
        Dictionary<string, string> inherited, string existingStyle)
    {
        if (inherited.Count == 0)
        {
            return existingStyle;
        }

        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existingStyle))
        {
            foreach (var part in existingStyle.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIdx = part.IndexOf(':');
                if (colonIdx > 0)
                {
                    existing[part[..colonIdx].Trim()] = part[(colonIdx + 1)..].Trim();
                }
            }
        }

        // Inherited styles fill in gaps — don't override explicit styles
        foreach (var (key, value) in inherited)
        {
            if (!existing.ContainsKey(key))
            {
                existing[key] = value;
            }
        }

        var sb = new StringBuilder();
        foreach (var (key, value) in existing)
        {
            if (sb.Length > 0)
            {
                sb.Append(';');
            }
            sb.Append(key).Append(':').Append(value);
        }
        return sb.ToString();
    }

    private static void AppendStyleString(StringBuilder sb, Dictionary<string, string> styles)
    {
        var first = true;
        foreach (var (key, value) in styles)
        {
            if (!first)
            {
                sb.Append(';');
            }
            sb.Append(key).Append(':').Append(value);
            first = false;
        }
    }

    private static bool IsInheritable(string property)
    {
        foreach (var prop in s_inheritableProps)
        {
            if (string.Equals(prop, property, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
