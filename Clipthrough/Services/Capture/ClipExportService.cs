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

    public async Task<ClipExportResult> ExportAsync(ClipEntry clip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var exportRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Clipthrough Exports");
        var clipName = BuildClipFileName(clip);
        var folderName = $"{SanitizePathSegment(clipName)}-{clip.Id}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var exportDirectory = Path.Combine(exportRoot, folderName);
        Directory.CreateDirectory(exportDirectory);

        var originalPath = await ExportOriginalPayloadAsync(exportDirectory, clip, clipName, cancellationToken);
        var renderedPath = await ExportRenderedTextAsync(exportDirectory, clip, clipName, cancellationToken);
        await ExportMetadataAsync(exportDirectory, clip, cancellationToken);

        return new ClipExportResult(exportDirectory, renderedPath ?? originalPath);
    }

    private static async Task<string> ExportOriginalPayloadAsync(string exportDirectory, ClipEntry clip, string clipName, CancellationToken cancellationToken)
    {
        var extension = GetOriginalFileExtension(clip);
        var path = Path.Combine(exportDirectory, $"{SanitizePathSegment(clipName)}{extension}");

        if (clip.ContentBytes is { Length: > 0 } bytes)
        {
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return path;
        }

        var content = clip.ContentType == ContentType.Image
            ? ClipDisplayFormatter.GetRawContentDisplay(clip)
            : clip.Content;
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
        return path;
    }

    private static async Task<string?> ExportRenderedTextAsync(string exportDirectory, ClipEntry clip, string clipName, CancellationToken cancellationToken)
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
            return null;
        }

        var path = Path.Combine(exportDirectory, $"{SanitizePathSegment(clipName)}-rendered.txt");
        await File.WriteAllTextAsync(path, text, Encoding.UTF8, cancellationToken);
        return path;
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

    private static string BuildClipFileName(ClipEntry clip)
    {
        if (clip.ContentType == ContentType.Image && ClipDisplayFormatter.TryGetPreferredImageLabel(clip) is { } imageLabel)
        {
            return imageLabel;
        }

        if (ClipDisplayFormatter.BuildFileItems(clip.Content) is { Count: > 0 } fileItems)
        {
            return Path.GetFileNameWithoutExtension(fileItems[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (!string.IsNullOrWhiteSpace(clip.Content))
        {
            return ClipDisplayFormatter.BuildTitle(clip.Content, clip.ContentType);
        }

        return clip.SourceApp ?? "clip";
    }

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
