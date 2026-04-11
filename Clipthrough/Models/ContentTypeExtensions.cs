using Clipthrough.Localization;

namespace Clipthrough.Models;

public static class ContentTypeExtensions
{
    public static string ToStorageValue(this ContentType contentType) => contentType switch
    {
        ContentType.Text => "text",
        ContentType.Image => "image",
        ContentType.RichText => "richtext",
        ContentType.Files => "files",
        _ => "text",
    };

    public static string ToDisplayName(this ContentType contentType) => AppText.GetContentTypeLabel(contentType);

    public static ContentType FromStorageValue(string? value) => value?.ToLowerInvariant() switch
    {
        "image" => ContentType.Image,
        "richtext" => ContentType.RichText,
        "files" => ContentType.Files,
        _ => ContentType.Text,
    };

    public static ContentType? FromFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, AppText.GetFilterContentTypeLabel(null), System.StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var contentType in System.Enum.GetValues<ContentType>())
        {
            if (string.Equals(value, AppText.GetContentTypeLabel(contentType), System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, contentType.ToString(), System.StringComparison.OrdinalIgnoreCase))
            {
                return contentType;
            }
        }

        return null;
    }
}

