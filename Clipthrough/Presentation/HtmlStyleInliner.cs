using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Clipthrough.Presentation;

/// <summary>
/// Preprocesses clipboard HTML so AvRichTextBox can render it with colors.
/// AvRichTextBox's LoadHtml supports inline style="color:..." on span/p elements
/// but does NOT support style blocks with CSS classes, div, or pre tags.
/// This inliner converts unsupported patterns to supported inline-styled spans.
/// </summary>
public static partial class HtmlStyleInliner
{
    private static readonly string[] s_inheritableProps =
        ["color", "font-family", "font-size", "font-weight", "font-style", "background-color"];

    [GeneratedRegex(@"<style[^>]*>(.*?)</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StyleBlockRegex();

    [GeneratedRegex(@"\.([a-zA-Z_][\w-]*)\s*\{([^}]*)\}", RegexOptions.Singleline)]
    private static partial Regex CssRuleRegex();

    [GeneratedRegex(@"style\s*=\s*""([^""]*?)""", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttrRegex();

    // Wrapper tags are just layout containers — strip them entirely after propagating styles
    [GeneratedRegex(@"<(div|article|section|main|header|footer|nav|aside)\b([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex WrapperOpenTagRegex();

    [GeneratedRegex(@"</(div|article|section|main|header|footer|nav|aside)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex WrapperCloseTagRegex();

    // Content block tags contain actual text — convert to <p>
    [GeneratedRegex(@"<pre\b([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex PreOpenTagRegex();

    [GeneratedRegex(@"</pre\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex PreCloseTagRegex();

    [GeneratedRegex(@"background-color\s*:\s*(#[0-9A-Fa-f]{3,8}|rgb\s*\([^)]+\))", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundColorRegex();

    [GeneratedRegex(@"(?<!background-)color\s*:\s*#([0-9A-Fa-f]{6})", RegexOptions.IgnoreCase)]
    private static partial Regex HexTextColorRegex();

    [GeneratedRegex(@"(?<!background-)color\s*:\s*rgb\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RgbTextColorRegex();

    [GeneratedRegex(@"rgb\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RgbColorValueRegex();

    public static string Inline(string html)
    {
        return Inline(html, out _);
    }

    public static string Inline(string html, out string? backgroundColor)
    {
        backgroundColor = null;

        if (string.IsNullOrWhiteSpace(html))
        {
            return html;
        }

        // Extract background color from the first styled element before any transformations
        backgroundColor = ExtractBackgroundColor(html);

        var cssRules = ExtractCssRules(html);

        var result = StyleBlockRegex().Replace(html, string.Empty);

        if (cssRules.Count > 0)
        {
            result = InlineClassStyles(result, cssRules);
        }

        result = PreservePreWhitespace(result);
        result = PropagateContainerStyles(result);
        result = ConvertUnsupportedTags(result);
        result = NormalizeRgbColors(result);

        return result;
    }

    /// <summary>
    /// Extracts the first background-color value from an inline style in the HTML.
    /// Used to set the control's background to match the content's intended theme.
    /// </summary>
    public static string? ExtractBackgroundColor(string html)
    {
        var match = BackgroundColorRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Analyzes text color values in the HTML. If the predominant text color is very light
    /// (designed for a dark background) but no explicit background is set, returns a dark
    /// background color. Returns null if text colors are dark or absent.
    /// </summary>
    public static string? InferBackgroundFromTextColors(string html)
    {
        var totalLuminance = 0.0;
        var count = 0;

        foreach (Match m in HexTextColorRegex().Matches(html))
        {
            var hex = m.Groups[1].Value;
            var r = Convert.ToInt32(hex[..2], 16);
            var g = Convert.ToInt32(hex[2..4], 16);
            var b = Convert.ToInt32(hex[4..6], 16);
            totalLuminance += (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            count++;
        }

        foreach (Match m in RgbTextColorRegex().Matches(html))
        {
            var r = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var g = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var b = int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            totalLuminance += (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            count++;
        }

        if (count == 0)
        {
            return null;
        }

        var avgLuminance = totalLuminance / count;
        return avgLuminance > 0.65 ? "#1E1E1E" : null;
    }

    private static Dictionary<string, string> ExtractCssRules(string html)
    {
        var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match styleBlock in StyleBlockRegex().Matches(html))
        {
            var cssText = styleBlock.Groups[1].Value;

            foreach (Match rule in CssRuleRegex().Matches(cssText))
            {
                var className = rule.Groups[1].Value;
                var properties = NormalizeCssProperties(rule.Groups[2].Value);
                rules[className] = properties;
            }
        }

        return rules;
    }

    private static string NormalizeCssProperties(string css)
    {
        var sb = new StringBuilder();
        foreach (var part in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append(';');
                }
                sb.Append(trimmed);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Inlines CSS class rules onto elements. When an element has both class and
    /// existing inline style, the styles are merged (existing inline styles take precedence).
    /// </summary>
    private static string InlineClassStyles(string html, Dictionary<string, string> cssRules)
    {
        // Match full opening tags that have a class attribute
        var tagWithClassRegex = new Regex(
            @"(<\w+\b)([^>]*?)\bclass\s*=\s*""([^""]+)""([^>]*>)",
            RegexOptions.IgnoreCase);

        return tagWithClassRegex.Replace(html, match =>
        {
            var tagStart = match.Groups[1].Value;
            var beforeClass = match.Groups[2].Value;
            var classNames = match.Groups[3].Value;
            var afterClass = match.Groups[4].Value;

            // Collect CSS properties from all matching classes
            var classProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cls in classNames.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (cssRules.TryGetValue(cls, out var propsStr))
                {
                    foreach (var (key, value) in ParseStyleProperties(propsStr))
                    {
                        classProps[key] = value;
                    }
                }
            }

            if (classProps.Count == 0)
            {
                return match.Value;
            }

            // Check for existing inline style in the remaining attributes
            var remainingAttrs = beforeClass + afterClass;
            var existingStyleMatch = StyleAttrRegex().Match(remainingAttrs);

            if (existingStyleMatch.Success)
            {
                // Merge: existing inline styles override class-derived styles
                var existingProps = ParseStyleProperties(existingStyleMatch.Groups[1].Value);
                foreach (var (key, value) in existingProps)
                {
                    classProps[key] = value;
                }

                var mergedStyle = BuildStyleString(classProps);
                var newAttrs = remainingAttrs[..existingStyleMatch.Index]
                    + $"style=\"{mergedStyle}\""
                    + remainingAttrs[(existingStyleMatch.Index + existingStyleMatch.Length)..];
                return tagStart + newAttrs;
            }

            // No existing style — just add new style
            var style = BuildStyleString(classProps);
            return $"{tagStart}{beforeClass}style=\"{style}\"{afterClass}";
        });
    }

    /// <summary>
    /// Propagates inheritable CSS properties from container tags (div, pre) to child
    /// span elements. Only processes spans that are positionally inside each container.
    /// Handles nested containers correctly (inner overrides outer).
    /// </summary>
    private static string PropagateContainerStyles(string html)
    {
        var containerRegex = new Regex(
            @"<(div|pre)\b[^>]*?\bstyle\s*=\s*""([^""]+)""[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Collect all containers with their range and inheritable styles
        var containers = new List<(int innerStart, int innerEnd, Dictionary<string, string> styles)>();

        foreach (Match match in containerRegex.Matches(html))
        {
            var tagName = match.Groups[1].Value;
            var containerStyles = ParseStyleProperties(match.Groups[2].Value);

            var inheritable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in s_inheritableProps)
            {
                if (containerStyles.TryGetValue(prop, out var value))
                {
                    inheritable[prop] = value;
                }
            }

            if (inheritable.Count == 0)
            {
                continue;
            }

            var closeTag = $"</{tagName}>";
            var closeIndex = html.IndexOf(closeTag, match.Index + match.Length, StringComparison.OrdinalIgnoreCase);
            if (closeIndex < 0)
            {
                continue;
            }

            containers.Add((match.Index + match.Length, closeIndex, inheritable));
        }

        if (containers.Count == 0)
        {
            return html;
        }

        // For each span, find enclosing containers and apply their inheritable styles
        var spanRegex = new Regex(@"<span\b([^>]*)>", RegexOptions.IgnoreCase);
        return spanRegex.Replace(html, spanMatch =>
        {
            var spanPos = spanMatch.Index;

            // Merge inheritable styles from all enclosing containers (outer first, inner overrides)
            var applicable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (innerStart, innerEnd, styles) in containers)
            {
                if (spanPos >= innerStart && spanPos < innerEnd)
                {
                    foreach (var (prop, value) in styles)
                    {
                        applicable[prop] = value;
                    }
                }
            }

            if (applicable.Count == 0)
            {
                return spanMatch.Value;
            }

            var attrs = spanMatch.Groups[1].Value;
            var existingStyleMatch = StyleAttrRegex().Match(attrs);

            if (existingStyleMatch.Success)
            {
                // Add missing inheritable properties (don't override existing)
                var existingProps = ParseStyleProperties(existingStyleMatch.Groups[1].Value);
                var merged = new StringBuilder(existingStyleMatch.Groups[1].Value);
                foreach (var (prop, value) in applicable)
                {
                    if (!existingProps.ContainsKey(prop))
                    {
                        if (merged.Length > 0 && merged[^1] != ';')
                        {
                            merged.Append(';');
                        }
                        merged.Append(prop).Append(':').Append(value);
                    }
                }
                var newStyle = $"style=\"{merged}\"";
                return "<span"
                    + attrs[..existingStyleMatch.Index]
                    + newStyle
                    + attrs[(existingStyleMatch.Index + existingStyleMatch.Length)..]
                    + ">";
            }

            // No existing style
            return $"<span style=\"{BuildStyleString(applicable)}\"{attrs}>";
        });
    }

    /// <summary>
    /// Converts whitespace inside &lt;pre&gt; blocks to HTML entities so it
    /// survives conversion to inline &lt;span&gt; elements.
    /// </summary>
    private static string PreservePreWhitespace(string html)
    {
        var preRegex = new Regex(
            @"(<pre\b[^>]*>)(.*?)(</pre\s*>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return preRegex.Replace(html, match =>
        {
            var openTag = match.Groups[1].Value;
            var content = match.Groups[2].Value;
            var closeTag = match.Groups[3].Value;

            // Replace actual newlines (not <br>) with <br/>
            content = content.Replace("\r\n", "<br/>").Replace("\n", "<br/>");

            // Replace runs of 2+ spaces with &nbsp; sequences
            content = Regex.Replace(content, @"  +", spaces =>
            {
                var sb = new StringBuilder();
                for (var i = 0; i < spaces.Length; i++)
                {
                    sb.Append(i % 2 == 0 ? "&nbsp;" : " ");
                }
                return sb.ToString();
            });

            return openTag + content + closeTag;
        });
    }

    private static Dictionary<string, string> ParseStyleProperties(string style)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = part[..colonIndex].Trim();
                var value = part[(colonIndex + 1)..].Trim();
                props[key] = value;
            }
        }
        return props;
    }

    private static string BuildStyleString(Dictionary<string, string> props)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in props)
        {
            if (sb.Length > 0)
            {
                sb.Append(';');
            }
            sb.Append(key).Append(':').Append(value);
        }
        return sb.ToString();
    }

    private static string ConvertUnsupportedTags(string html)
    {
        // Strip wrapper tags entirely (styles already propagated to children)
        var result = WrapperOpenTagRegex().Replace(html, string.Empty);
        result = WrapperCloseTagRegex().Replace(result, string.Empty);

        // Convert <pre> to <p> (these contain actual text content)
        result = PreOpenTagRegex().Replace(result, match =>
        {
            var attrs = match.Groups[1].Value;
            return $"<p{attrs}>";
        });
        result = PreCloseTagRegex().Replace(result, "</p>");

        return result;
    }

    /// <summary>
    /// Converts all rgb(R, G, B) color values to #RRGGBB hex format.
    /// AvRichTextBox's HTML parser does not support rgb() CSS color values.
    /// </summary>
    private static string NormalizeRgbColors(string html)
    {
        return RgbColorValueRegex().Replace(html, match =>
        {
            if (int.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var r)
                && int.TryParse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out var g)
                && int.TryParse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture, out var b))
            {
                return $"#{Math.Clamp(r, 0, 255):X2}{Math.Clamp(g, 0, 255):X2}{Math.Clamp(b, 0, 255):X2}";
            }

            return match.Value;
        });
    }
}
