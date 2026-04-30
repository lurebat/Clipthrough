using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clipthrough.Services;

public static partial class TextTransformationService
{
    private const int TabWidth = 4;

    public static string Apply(Models.TextTransformation transformation, string input, string? delimiter = null)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return transformation switch
        {
            Models.TextTransformation.None => input,
            Models.TextTransformation.UpperCase => input.ToUpper(CultureInfo.CurrentCulture),
            Models.TextTransformation.LowerCase => input.ToLower(CultureInfo.CurrentCulture),
            Models.TextTransformation.TitleCase => ToTitleCase(input),
            Models.TextTransformation.SentenceCase => ToSentenceCase(input),
            Models.TextTransformation.UpperCamelCase => ToUpperCamelCase(input),
            Models.TextTransformation.LowerCamelCase => ToLowerCamelCase(input),
            Models.TextTransformation.FromCamelCase => FromCamelCase(input),
            Models.TextTransformation.TrimWhitespace => TrimWhitespace(input),
            Models.TextTransformation.CollapseWhitespace => CollapseWhitespace(input),
            Models.TextTransformation.TabsToSpaces => input.Replace("\t", new string(' ', TabWidth), StringComparison.Ordinal),
            Models.TextTransformation.SpacesToTabs => ConvertLeadingSpacesToTabs(input),
            Models.TextTransformation.NormalizeEol => NormalizeEol(input),
            Models.TextTransformation.LinesToJsonArray => LinesToJsonArray(input),
            Models.TextTransformation.JoinWithDelimiter => JoinWithDelimiter(input, delimiter ?? ", "),
            Models.TextTransformation.SortLines => SortLines(input),
            Models.TextTransformation.ReverseLines => ReverseLines(input),
            Models.TextTransformation.RemoveEmptyLines => RemoveEmptyLines(input),
            Models.TextTransformation.RemoveDuplicateLines => RemoveDuplicateLines(input),
            Models.TextTransformation.BoxTableToHtml => BoxTableToHtml(input),
            _ => input,
        };
    }

    private static string ToTitleCase(string input)
        => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(input.ToLower(CultureInfo.CurrentCulture));

    private static string ToSentenceCase(string input)
    {
        var lines = SplitLines(input);
        var sb = new StringBuilder(input.Length);

        foreach (var line in lines)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append(line);
                continue;
            }

            var lower = line.ToLower(CultureInfo.CurrentCulture);
            var capitalizeNext = true;
            for (var i = 0; i < lower.Length; i++)
            {
                var c = lower[i];
                if (capitalizeNext && char.IsLetter(c))
                {
                    sb.Append(char.ToUpper(c, CultureInfo.CurrentCulture));
                    capitalizeNext = false;
                }
                else
                {
                    sb.Append(c);
                    if (c is '.' or '!' or '?')
                    {
                        capitalizeNext = true;
                    }
                }
            }
        }

        return sb.ToString();
    }

    private static string ToUpperCamelCase(string input)
    {
        var words = WordBoundaryRegex().Split(input).Where(static w => w.Length > 0);
        return string.Concat(words.Select(static w =>
            char.ToUpper(w[0], CultureInfo.CurrentCulture) + w[1..].ToLower(CultureInfo.CurrentCulture)));
    }

    private static string ToLowerCamelCase(string input)
    {
        var words = WordBoundaryRegex().Split(input).Where(static w => w.Length > 0).ToArray();
        if (words.Length == 0)
        {
            return input;
        }

        var sb = new StringBuilder();
        sb.Append(words[0].ToLower(CultureInfo.CurrentCulture));
        for (var i = 1; i < words.Length; i++)
        {
            sb.Append(char.ToUpper(words[i][0], CultureInfo.CurrentCulture));
            sb.Append(words[i][1..].ToLower(CultureInfo.CurrentCulture));
        }

        return sb.ToString();
    }

    private static string FromCamelCase(string input)
    {
        var result = CamelCaseBoundaryRegex().Replace(input, " $1");
        return result[..1].ToUpper(CultureInfo.CurrentCulture) + result[1..];
    }

    private static string TrimWhitespace(string input)
    {
        var lines = SplitLines(input);
        return string.Join('\n', lines.Select(static line => line.Trim()));
    }

    private static string CollapseWhitespace(string input)
        => MultipleWhitespaceRegex().Replace(input.Trim(), " ");

    private static string ConvertLeadingSpacesToTabs(string input)
    {
        var lines = SplitLines(input);
        var sb = new StringBuilder(input.Length);
        var spaces = new string(' ', TabWidth);

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            var line = lines[i];
            var pos = 0;
            while (pos + TabWidth <= line.Length && line.AsSpan(pos, TabWidth).SequenceEqual(spaces.AsSpan()))
            {
                sb.Append('\t');
                pos += TabWidth;
            }

            sb.Append(line.AsSpan(pos));
        }

        return sb.ToString();
    }

    private static string NormalizeEol(string input)
        => input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    private static string LinesToJsonArray(string input)
    {
        var lines = SplitLines(NormalizeEol(input));
        return JsonSerializer.Serialize(lines);
    }

    private static string JoinWithDelimiter(string input, string delimiter)
    {
        var lines = SplitLines(NormalizeEol(input));
        return string.Join(delimiter, lines.Where(static line => !string.IsNullOrEmpty(line)));
    }

    private static string SortLines(string input)
    {
        var lines = SplitLines(NormalizeEol(input));
        Array.Sort(lines, StringComparer.CurrentCulture);
        return string.Join('\n', lines);
    }

    private static string ReverseLines(string input)
    {
        var lines = SplitLines(NormalizeEol(input));
        Array.Reverse(lines);
        return string.Join('\n', lines);
    }

    private static string RemoveEmptyLines(string input)
    {
        var lines = SplitLines(NormalizeEol(input));
        return string.Join('\n', lines.Where(static line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string RemoveDuplicateLines(string input)
    {
        var lines = SplitLines(NormalizeEol(input));
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        return string.Join('\n', lines.Where(line => seen.Add(line)));
    }

    private static string BoxTableToHtml(string input)
    {
        var lines = SplitLines(NormalizeEol(input));
        var sb = new StringBuilder();
        var block = new System.Collections.Generic.List<string>();
        var nonTableBuffer = new System.Collections.Generic.List<string>();
        var producedAnyTable = false;

        void FlushBlock()
        {
            if (block.Count == 0)
            {
                return;
            }

            var html = TryRenderTableBlock(block);
            if (html is not null)
            {
                FlushNonTable();
                sb.Append(html);
                producedAnyTable = true;
            }
            else
            {
                // Not actually a table — treat the buffered lines as plain text.
                foreach (var raw in block)
                {
                    nonTableBuffer.Add(raw);
                }
            }
            block.Clear();
        }

        void FlushNonTable()
        {
            if (nonTableBuffer.Count == 0)
            {
                return;
            }

            sb.Append("<div>");
            for (var i = 0; i < nonTableBuffer.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append("<br>");
                }
                sb.Append(EscapeHtml(nonTableBuffer[i]));
            }
            sb.Append("</div>");
            nonTableBuffer.Clear();
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length > 0 && (IsTableBorderLine(trimmed) || IsRowSeparator(trimmed[0])))
            {
                block.Add(rawLine);
                continue;
            }

            FlushBlock();
            nonTableBuffer.Add(rawLine);
        }

        FlushBlock();
        FlushNonTable();

        return producedAnyTable ? sb.ToString() : input;
    }

    private static string? TryRenderTableBlock(System.Collections.Generic.List<string> blockLines)
    {
        var rows = new System.Collections.Generic.List<string[]>();
        foreach (var rawLine in blockLines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || IsTableBorderLine(trimmed))
            {
                continue;
            }
            if (!IsRowSeparator(trimmed[0]))
            {
                continue;
            }

            var cells = SplitOnRowSeparator(trimmed);
            if (cells.Length == 0)
            {
                continue;
            }

            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = cells[i].Trim();
            }
            rows.Add(cells);
        }

        if (rows.Count == 0)
        {
            return null;
        }

        var columnCount = rows.Max(static r => r.Length);
        var sb = new StringBuilder();
        sb.Append("<table border=\"1\" cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse;\">");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var tag = rowIndex == 0 ? "th" : "td";
            sb.Append("<tr>");
            for (var col = 0; col < columnCount; col++)
            {
                var cell = col < row.Length ? row[col] : string.Empty;
                sb.Append('<').Append(tag).Append('>');
                sb.Append(EscapeHtml(cell));
                sb.Append("</").Append(tag).Append('>');
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    private static bool IsRowSeparator(char c)
        => c is '│' or '┃' or '|' or '║';

    private static bool IsTableBorderLine(string line)
    {
        // A border line has no letters/digits and contains at least one
        // horizontal/junction character (dash, equals, plus, or any non-vertical
        // box-drawing glyph). Pure vertical-only lines (e.g. just "|") are not
        // borders — they would be empty data rows instead.
        var hasHorizontal = false;
        foreach (var c in line)
        {
            if (char.IsLetterOrDigit(c))
            {
                return false;
            }

            if (c is '-' or '=' or '+' or ':')
            {
                hasHorizontal = true;
                continue;
            }

            if (c >= '\u2500' && c <= '\u257F')
            {
                if (!IsRowSeparator(c))
                {
                    hasHorizontal = true;
                }
                continue;
            }

            if (IsRowSeparator(c) || char.IsWhiteSpace(c))
            {
                continue;
            }

            // Any other character (punctuation, symbols) means it's content.
            return false;
        }

        return hasHorizontal;
    }

    private static string[] SplitOnRowSeparator(string line)
    {
        // Drop the leading and trailing vertical separator, then split on any
        // vertical separator character within the remainder.
        var start = 0;
        var end = line.Length;
        if (start < end && IsRowSeparator(line[start])) start++;
        if (end > start && IsRowSeparator(line[end - 1])) end--;

        if (end <= start)
        {
            return Array.Empty<string>();
        }

        var inner = line[start..end];
        var parts = new System.Collections.Generic.List<string>();
        var sb = new StringBuilder();
        foreach (var c in inner)
        {
            if (IsRowSeparator(c))
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        parts.Add(sb.ToString());
        return parts.ToArray();
    }

    private static string EscapeHtml(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string[] SplitLines(string input)
        => input.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n');

    [GeneratedRegex(@"[\s_\-]+")]
    private static partial Regex WordBoundaryRegex();

    [GeneratedRegex(@"(?<!^)([A-Z])")]
    private static partial Regex CamelCaseBoundaryRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleWhitespaceRegex();
}
