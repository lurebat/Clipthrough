using System;

namespace Clipthrough.Presentation;

/// <summary>
/// Cheap pre-checks for handing clip markup to a VellumText importer. Each
/// importer is linear for ordinary input, but two shapes are not, and both
/// arrive on the clipboard: a run with no break opportunity (quadratic line
/// breaking in the preview) and deep nesting (recursion that overflows the
/// stack). A stack overflow cannot be caught - the whole process dies - so the
/// only defence is not to start.
/// </summary>
internal static class ImportGuards
{
    /// <summary>
    /// The longest run without a break opportunity worth importing. See
    /// RichDocumentView for the measurements behind it.
    /// </summary>
    public const int MaxUnbrokenRunChars = 10_000;

    /// <summary>
    /// The deepest nesting worth importing. Markdown→HTML died with 0xC00000FD
    /// at 8,192 nested <c>[</c> and survived 7,000 (F-48); an HTML import of
    /// 990 nested elements took 0.65 s. Nothing a person writes nests a
    /// hundredth as deep, so this leaves a wide margin on both sides.
    /// </summary>
    public const int MaxNestingDepth = 1_000;

    private const int MaxTagLength = 1_024;

    public static int LongestUnbrokenRun(string content)
    {
        var longest = 0;
        var current = 0;
        foreach (var c in content)
        {
            if (char.IsWhiteSpace(c))
            {
                current = 0;
                continue;
            }

            current++;
            if (current > longest)
            {
                longest = current;
            }
        }

        return longest;
    }

    /// <summary>
    /// The deepest nesting the text could produce in either importer, stopping
    /// once it passes <paramref name="limit"/>: open brackets, blockquote
    /// markers and list indentation for Markdown, open tags for HTML. It
    /// over-counts rather than under-counts - a stray <c>&lt;</c> or an
    /// unclosed bracket is counted as nesting - because a false refusal costs
    /// one unconverted clip and a false pass costs the process.
    /// </summary>
    public static int NestingDepth(string content, int limit)
    {
        var deepest = 0;
        var brackets = 0;
        var tags = 0;
        var atLineStart = true;
        var lineMarkers = 0;
        var indent = 0;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '\n')
            {
                atLineStart = true;
                lineMarkers = 0;
                indent = 0;
                continue;
            }

            if (atLineStart)
            {
                if (c == '>')
                {
                    lineMarkers++;
                }
                else if (c == ' ' || c == '\t')
                {
                    indent += c == '\t' ? 4 : 1;
                }
                else
                {
                    atLineStart = false;
                }
            }

            switch (c)
            {
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets = Math.Max(0, brackets - 1);
                    break;
                case '<' when i + 1 < content.Length:
                    var next = content[i + 1];
                    if (next == '/')
                    {
                        // A parser ignores a stray end tag, so one that closes
                        // nothing must not cancel an open: </p> between nested
                        // <template>s would otherwise hide the nesting entirely.
                        if (i + 2 < content.Length && IsNestingElement(content, i + 2))
                        {
                            tags = Math.Max(0, tags - 1);
                        }
                    }
                    else if (char.IsLetter(next) && OpensNesting(content, i + 1))
                    {
                        tags++;
                    }

                    break;
            }

            var depth = Math.Max(Math.Max(brackets, tags), Math.Max(lineMarkers, indent / 2));
            if (depth > deepest)
            {
                deepest = depth;
                if (deepest > limit)
                {
                    return deepest;
                }
            }
        }

        return deepest;
    }

    // Elements that never contain anything, and elements whose open tag closes
    // a previous sibling of the same kind: a document with thousands of <br>
    // or unclosed <li> is long, not deep, and must not be refused as nested.
    private static readonly string[] FlatElements =
    [
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
        "dd", "dt", "li", "option", "p", "td", "th", "tr",
    ];

    private static bool IsNestingElement(string content, int nameStart)
    {
        var nameEnd = nameStart;
        while (nameEnd < content.Length && char.IsLetterOrDigit(content[nameEnd]))
        {
            nameEnd++;
        }

        if (nameEnd == nameStart)
        {
            return false;
        }

        var name = content.AsSpan(nameStart, nameEnd - nameStart);
        foreach (var flat in FlatElements)
        {
            if (name.Equals(flat, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool OpensNesting(string content, int nameStart)
    {
        if (!IsNestingElement(content, nameStart))
        {
            return false;
        }

        // A self-closing tag (<x/>) opens nothing either. The look-ahead is
        // bounded: unbounded, "<a<a<a..." with no '>' made this scan quadratic.
        var close = content.IndexOf('>', nameStart, Math.Min(MaxTagLength, content.Length - nameStart));
        return close < 0 || content[close - 1] != '/';
    }

    /// <summary>
    /// True when <paramref name="content"/> is safe to hand to an importer on
    /// both counts.
    /// </summary>
    public static bool IsSafeToImport(string content)
        => LongestUnbrokenRun(content) <= MaxUnbrokenRunChars
            && NestingDepth(content, MaxNestingDepth) <= MaxNestingDepth;
}
