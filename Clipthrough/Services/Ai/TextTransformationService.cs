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
            Models.TextTransformation.JsonQuote => JsonQuote(input),
            Models.TextTransformation.JsonUnquote => JsonUnquote(input),
            Models.TextTransformation.JsonMinify => JsonReformat(input, indented: false),
            Models.TextTransformation.JsonPretty => JsonReformat(input, indented: true),
            Models.TextTransformation.UrlEncode => Uri.EscapeDataString(input),
            Models.TextTransformation.UrlDecode => SafeUrlDecode(input),
            Models.TextTransformation.Base64Encode => Convert.ToBase64String(Encoding.UTF8.GetBytes(input)),
            Models.TextTransformation.Base64Decode => SafeBase64Decode(input),
            Models.TextTransformation.CleanTerminalFormatting => CleanTerminalFormatting(input),
            _ => input,
        };
    }

    // Casing splits two ways, and the split is deliberate.
    //
    // UpperCase/LowerCase/TitleCase/SentenceCase are linguistic: the user is
    // recasing text written in their own language, so they follow
    // CurrentCulture. A Turkish user upper-casing "istanbul" wants "İSTANBUL";
    // forcing invariant here would produce "ISTANBUL", which is simply wrong
    // Turkish - the same class of corruption, just pointed the other way.
    //
    // The camel-case transforms below are structural: they produce identifiers,
    // where the Turkish dotless i is unambiguous corruption ("ID" -> "ıd",
    // "identifier" -> "İdentifier") and where output must not depend on the
    // machine's locale. Those use InvariantCulture.
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
            char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..].ToLower(CultureInfo.InvariantCulture)));
    }

    private static string ToLowerCamelCase(string input)
    {
        var words = WordBoundaryRegex().Split(input).Where(static w => w.Length > 0).ToArray();
        if (words.Length == 0)
        {
            return input;
        }

        var sb = new StringBuilder();
        sb.Append(words[0].ToLower(CultureInfo.InvariantCulture));
        for (var i = 1; i < words.Length; i++)
        {
            sb.Append(char.ToUpper(words[i][0], CultureInfo.InvariantCulture));
            sb.Append(words[i][1..].ToLower(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string FromCamelCase(string input)
    {
        var result = CamelCaseBoundaryRegex().Replace(input, " $1");
        return result[..1].ToUpper(CultureInfo.InvariantCulture) + result[1..];
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

        // InvariantCulture, not CurrentCulture and not Ordinal. CurrentCulture
        // made the same clip sort differently on two machines. Ordinal would be
        // deterministic but compares codepoints, so every capitalised line would
        // be banished ahead of every lowercase one ("Zebra" before "apple") and
        // accented letters would land past "z" - the opposite of what someone
        // asking to sort lines expects. Invariant is deterministic AND orders
        // like a dictionary.
        Array.Sort(lines, StringComparer.InvariantCulture);
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
        var lines = SplitTableInputLines(input);
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

    private static string[] SplitTableInputLines(string input)
    {
        var expanded = new System.Collections.Generic.List<string>();
        foreach (var line in SplitLines(NormalizeEol(input)))
        {
            expanded.AddRange(SplitCollapsedTableLine(line));
        }

        return expanded.ToArray();
    }

    private static string[] SplitCollapsedTableLine(string line)
    {
        if (line.Length == 0)
        {
            return [line];
        }

        var segments = new System.Collections.Generic.List<string>();
        var segmentStart = 0;
        for (var i = 1; i < line.Length; i++)
        {
            if (!StartsCollapsedTableSegment(line, i))
            {
                continue;
            }

            AddSegment(segmentStart, i);
            segmentStart = i;
        }

        if (segments.Count == 0)
        {
            return [line];
        }

        AddSegment(segmentStart, line.Length);
        return segments.ToArray();

        void AddSegment(int start, int end)
        {
            var segment = line[start..end];
            if (start > 0)
            {
                segment = segment.TrimStart();
            }
            if (end < line.Length)
            {
                segment = segment.TrimEnd();
            }

            if (segment.Length > 0)
            {
                segments.Add(segment);
            }
        }
    }

    private static bool StartsCollapsedTableSegment(string line, int index)
    {
        if (char.IsWhiteSpace(line[index]))
        {
            return false;
        }

        var previous = PreviousNonWhitespace(line, index - 1);
        if (previous is null)
        {
            return false;
        }

        var current = line[index];
        return (IsTableBorderStart(current)
                && (IsRowSeparator(previous.Value) || IsTableBorderEnd(previous.Value))
                && LooksLikeCollapsedBorderSegment(line, index))
            || (IsRowSeparator(current) && IsTableBorderEnd(previous.Value));
    }

    private static bool LooksLikeCollapsedBorderSegment(string line, int startIndex)
    {
        var end = startIndex;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
        {
            end++;
        }

        return IsTableBorderLine(line[startIndex..end]);
    }

    private static char? PreviousNonWhitespace(string line, int startIndex)
    {
        for (var i = startIndex; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(line[i]))
            {
                return line[i];
            }
        }

        return null;
    }

    private static bool IsTableBorderStart(char c)
        => c is '┌' or '├' or '└' or '╔' or '╠' or '╚' or '+';

    private static bool IsTableBorderEnd(char c)
        => c is '┐' or '┤' or '┘' or '╗' or '╣' or '╝' or '+';

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

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiEscapeRegex();

    // Only Unicode box-drawing & block-element chars count as "borders". ASCII
    // '|' / '+' / '-' are *not* borders here — those collide with legitimate
    // content (Kusto / SQL / shell pipes, options, math, etc.). If the user
    // pasted a true ASCII-bordered table they should use `BoxTableToHtml`.
    private static bool IsBoxBorderChar(char c) => c >= '\u2500' && c <= '\u259F';

    private static string JsonQuote(string input)
    {
        try
        {
            return JsonSerializer.Serialize(input);
        }
        catch (JsonException)
        {
            // Serializing a plain string into JSON should never throw, but
            // guard anyway so the user gets the original text rather than an
            // exception.
            return input;
        }
    }

    // Forgiving JSON-string unquote. If the input is a well-formed JSON string
    // literal (with or without surrounding quotes / leading whitespace), it
    // returns the decoded value. If the input is bare text containing escape
    // sequences (like "a\nb"), it applies the same escape rules. If neither
    // applies, the input is returned unchanged so the operation is safe.
    private static string JsonUnquote(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            return input;
        }

        // Wrap bare content in quotes when it isn't already a JSON string
        // literal, so we get one consistent decode path. We escape any
        // unescaped double quotes inside bare content to keep the literal
        // well-formed.
        string toParse;
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            toParse = trimmed;
        }
        else
        {
            var sb = new StringBuilder(trimmed.Length + 2);
            sb.Append('"');
            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (c == '"' && (i == 0 || trimmed[i - 1] != '\\'))
                {
                    sb.Append("\\\"");
                }
                else
                {
                    sb.Append(c);
                }
            }
            sb.Append('"');
            toParse = sb.ToString();
        }

        try
        {
            var result = JsonSerializer.Deserialize<string>(toParse);
            return result ?? input;
        }
        catch (JsonException)
        {
            // Bail out gracefully so a bad payload still returns something
            // useful for the user.
            return input;
        }
    }

    private static string JsonReformat(string input, bool indented)
    {
        try
        {
            using var doc = JsonDocument.Parse(input);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = indented });
        }
        catch (JsonException)
        {
            return input;
        }
    }

    private static string SafeUrlDecode(string input)
    {
        try
        {
            return Uri.UnescapeDataString(input);
        }
        catch (UriFormatException)
        {
            return input;
        }
    }

    private static string SafeBase64Decode(string input)
    {
        var candidate = CollapseWhitespace(input).Replace(" ", string.Empty, StringComparison.Ordinal);
        // Base64 strings are 4-byte aligned; pad if the user pasted without padding.
        var padding = (4 - candidate.Length % 4) % 4;
        if (padding > 0)
        {
            candidate += new string('=', padding);
        }

        try
        {
            var bytes = Convert.FromBase64String(candidate);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return input;
        }
    }

    // Pulls plain text out of CLI / TUI captures wrapped in box-drawing borders
    // and scrollbar columns (the bordered Kusto / Claude / Copilot CLI panels).
    // It:
    //   - strips ANSI color / CSI escape sequences,
    //   - drops lines that are 100% border characters (the top/bottom rules),
    //   - removes leading/trailing border + space runs on each line so the
    //     inner content survives even with double-bordered panels,
    //   - rtrims each line so the giant whitespace pad columns disappear, and
    //   - normalizes line endings.
    private static string CleanTerminalFormatting(string input)
    {
        var stripped = AnsiEscapeRegex().Replace(input, string.Empty);
        var lines = SplitLines(stripped);
        var output = new System.Collections.Generic.List<string>(lines.Length);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Replace("\0", string.Empty, StringComparison.Ordinal);

            // Drop pure-border rule lines (the top/bottom edges of a box).
            if (IsTerminalRule(line))
            {
                continue;
            }

            line = StripTerminalBorders(line);
            output.Add(line.TrimEnd());
        }

        // Drop leading/trailing blank lines introduced by stripped borders or
        // empty padding rows.
        var start = 0;
        var end = output.Count;
        while (start < end && output[start].Length == 0) start++;
        while (end > start && output[end - 1].Length == 0) end--;

        if (end <= start)
        {
            return input;
        }

        return string.Join('\n', output.GetRange(start, end - start));
    }

    // Removes leading box-drawing chars and up to two padding spaces (typical
    // column padding) and any trailing run that contains a box-drawing char
    // (the scrollbar / right border / `│ │` combo). Leading content
    // whitespace beyond the 2-space padding budget is preserved so code
    // indentation survives.
    private static string StripTerminalBorders(string line)
    {
        var start = 0;
        var sawLeadingBorder = false;
        while (start < line.Length && IsBoxBorderChar(line[start]))
        {
            start++;
            sawLeadingBorder = true;
        }
        if (sawLeadingBorder)
        {
            var padBudget = 2;
            while (start < line.Length && line[start] == ' ' && padBudget > 0)
            {
                start++;
                padBudget--;
            }
        }

        // Scan back from the end consuming spaces+borders; only commit the
        // truncation if at least one border was in that trailing run.
        var scan = line.Length;
        var sawTrailingBorder = false;
        while (scan > start)
        {
            var c = line[scan - 1];
            if (c == ' ')
            {
                scan--;
                continue;
            }
            if (IsBoxBorderChar(c))
            {
                sawTrailingBorder = true;
                scan--;
                continue;
            }
            break;
        }
        var finalEnd = sawTrailingBorder ? scan : line.Length;

        return line[start..finalEnd];
    }

    // A "rule" line is one that's made up only of box-drawing characters and
    // whitespace (with at least one box-drawing char). Plain ASCII rules like
    // `+----+----+` are intentionally not matched here so they survive into
    // BoxTableToHtml.
    private static bool IsTerminalRule(string line)
    {
        var sawBorder = false;
        foreach (var c in line)
        {
            if (c == ' ') continue;
            if (!IsBoxBorderChar(c))
            {
                return false;
            }
            sawBorder = true;
        }
        return sawBorder;
    }
}
