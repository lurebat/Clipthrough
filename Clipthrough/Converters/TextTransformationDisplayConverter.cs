using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Clipthrough.Models;

namespace Clipthrough.Converters;

public sealed class TextTransformationDisplayConverter : IValueConverter
{
    public static readonly TextTransformationDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TextTransformation transformation)
        {
            return value;
        }

        return transformation switch
        {
            TextTransformation.None => "Apply text transformation…",
            TextTransformation.UpperCase => "UPPERCASE",
            TextTransformation.LowerCase => "lowercase",
            TextTransformation.TitleCase => "Title Case",
            TextTransformation.SentenceCase => "Sentence case",
            TextTransformation.UpperCamelCase => "UpperCamelCase",
            TextTransformation.LowerCamelCase => "lowerCamelCase",
            TextTransformation.FromCamelCase => "From camelCase",
            TextTransformation.TrimWhitespace => "Trim whitespace",
            TextTransformation.CollapseWhitespace => "Collapse whitespace",
            TextTransformation.TabsToSpaces => "Tabs → Spaces",
            TextTransformation.SpacesToTabs => "Spaces → Tabs",
            TextTransformation.NormalizeEol => "Normalize line endings",
            TextTransformation.LinesToJsonArray => "Lines → JSON array",
            TextTransformation.JoinWithDelimiter => "Join lines with delimiter",
            TextTransformation.SortLines => "Sort lines",
            TextTransformation.ReverseLines => "Reverse lines",
            TextTransformation.RemoveEmptyLines => "Remove empty lines",
            TextTransformation.RemoveDuplicateLines => "Remove duplicate lines",
            TextTransformation.BoxTableToHtml => "Text table → HTML",
            _ => transformation.ToString(),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
