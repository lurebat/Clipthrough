namespace AvaloniaApplication1.Models;

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

    public static string ToDisplayName(this ContentType contentType) => contentType switch
    {
        ContentType.Text => "Text",
        ContentType.Image => "Image",
        ContentType.RichText => "Rich text",
        ContentType.Files => "Files",
        _ => "Text",
    };

    public static ContentType FromStorageValue(string? value) => value?.ToLowerInvariant() switch
    {
        "image" => ContentType.Image,
        "richtext" => ContentType.RichText,
        "files" => ContentType.Files,
        _ => ContentType.Text,
    };

    public static ContentType? FromFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "All", System.StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return System.Enum.TryParse<ContentType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }
}

