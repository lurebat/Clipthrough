namespace Clipthrough.Models;

public static class ClipContentFormatExtensions
{
    public static string ToStorageValue(this ClipContentFormat format) => format switch
    {
        ClipContentFormat.Html => "html",
        ClipContentFormat.Rtf => "rtf",
        ClipContentFormat.Bitmap => "bitmap",
        ClipContentFormat.FileList => "files",
        _ => "text",
    };

    public static ClipContentFormat FromStorageValue(string? value) => value?.ToLowerInvariant() switch
    {
        "html" => ClipContentFormat.Html,
        "rtf" => ClipContentFormat.Rtf,
        "bitmap" => ClipContentFormat.Bitmap,
        "files" => ClipContentFormat.FileList,
        _ => ClipContentFormat.PlainText,
    };
}
