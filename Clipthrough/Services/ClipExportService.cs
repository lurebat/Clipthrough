using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Presentation;

namespace Clipthrough.Services;

public sealed class ClipExportService : IClipExportService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string> ExportAsync(ClipEntry clip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var exportRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Clipthrough Exports");
        var folderName = $"{SanitizePathSegment(clip.SourceApp ?? "clip")}-{clip.Id}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var exportDirectory = Path.Combine(exportRoot, folderName);
        Directory.CreateDirectory(exportDirectory);

        await ExportOriginalPayloadAsync(exportDirectory, clip, cancellationToken);
        await ExportRenderedTextAsync(exportDirectory, clip, cancellationToken);
        await ExportMetadataAsync(exportDirectory, clip, cancellationToken);

        return exportDirectory;
    }

    private static async Task ExportOriginalPayloadAsync(string exportDirectory, ClipEntry clip, CancellationToken cancellationToken)
    {
        var extension = GetOriginalFileExtension(clip);
        var path = Path.Combine(exportDirectory, $"original{extension}");

        if (clip.ContentBytes is { Length: > 0 } bytes)
        {
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return;
        }

        var content = clip.ContentType == ContentType.Image
            ? ClipDisplayFormatter.GetRawContentDisplay(clip)
            : clip.Content;
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
    }

    private static async Task ExportRenderedTextAsync(string exportDirectory, ClipEntry clip, CancellationToken cancellationToken)
    {
        var text = clip.ContentType switch
        {
            ContentType.RichText => ClipDisplayFormatter.RenderRichContent(ClipDisplayFormatter.GetRawContentDisplay(clip)),
            ContentType.Text => clip.Content,
            ContentType.Files => clip.Content,
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await File.WriteAllTextAsync(Path.Combine(exportDirectory, "rendered.txt"), text, Encoding.UTF8, cancellationToken);
    }

    private static async Task ExportMetadataAsync(string exportDirectory, ClipEntry clip, CancellationToken cancellationToken)
    {
        var metadata = new
        {
            clip.Id,
            ContentType = clip.ContentType.ToStorageValue(),
            ContentFormat = clip.ContentFormat.ToStorageValue(),
            clip.SourceApp,
            clip.SourceAppPath,
            clip.IsFavorite,
            clip.IsSensitive,
            clip.CopyCount,
            FirstCopiedAt = clip.FirstCopiedAt,
            LastCopiedAt = clip.LastCopiedAt,
            clip.ByteSize,
            clip.ImageWidth,
            clip.ImageHeight,
            clip.Hash,
            SensitivityMatches = clip.SensitivityMatches,
            RawContent = ClipDisplayFormatter.GetRawContentDisplay(clip),
            RenderedPreview = ClipDisplayFormatter.BuildPreviewSnippet(clip),
        };

        await File.WriteAllTextAsync(
            Path.Combine(exportDirectory, "metadata.json"),
            JsonSerializer.Serialize(metadata, s_jsonOptions),
            cancellationToken);
    }

    private static string GetOriginalFileExtension(ClipEntry clip) => clip.ContentFormat switch
    {
        ClipContentFormat.Html => ".html",
        ClipContentFormat.Rtf => ".rtf",
        ClipContentFormat.Bitmap => ".png",
        ClipContentFormat.FileList => ".txt",
        _ => ".txt",
    };

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalidChars, character) >= 0 ? '_' : character);
        }

        return string.IsNullOrWhiteSpace(builder.ToString()) ? "clip" : builder.ToString().Trim();
    }
}
