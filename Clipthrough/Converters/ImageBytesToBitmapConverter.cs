using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Clipthrough.Converters;

public sealed class ImageBytesToBitmapConverter : IValueConverter
{
    public static readonly ImageBytesToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
