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
