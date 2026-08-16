using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Clipthrough.Models;
using Clipthrough.Services.Platform;

namespace Clipthrough.Services;

/// <summary>
/// Marker applied to <see cref="ClipCaptureRequest.ImportKind"/> and
/// <see cref="ClipEntry.ImportKind"/> when a clip enters via drag-and-drop.
/// </summary>
public static class ClipImportKinds
{
    public const string DragDrop = "drag_drop";
}

public sealed class DragDropService : IDragDropService
{
    private static readonly string[] HtmlFormatNames = ["HTML Format", "text/html", "public.html"];
    private static readonly string[] RtfFormatNames = ["Rich Text Format", "text/rtf", "public.rtf"];
    private const string PngFormatName = "PNG";

    private static readonly DataFormat<string> CfHtmlFormat = DataFormat.CreateStringPlatformFormat("HTML Format");
    private static readonly DataFormat<string> RtfFormat = DataFormat.CreateStringPlatformFormat("Rich Text Format");
    private static readonly DataFormat<byte[]> PngBytesFormat = DataFormat.CreateBytesPlatformFormat(PngFormatName);

    /// <summary>
    /// Directory used to stage image-clip files during drag-out. Files are
    /// reaped by <c>ClipStoreService.ApplyMaintenanceAsync</c>.
    /// </summary>
    public static string DragTempDirectory { get; } = Path.Combine(Path.GetTempPath(), "Clipthrough", "drag");

    public async Task<IDataTransfer> BuildDragPayloadAsync(IReadOnlyList<ClipEntry> clips, IStorageProvider storageProvider)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(storageProvider);

        var transfer = new DataTransfer();
        if (clips.Count == 0)
        {
            return transfer;
        }

        if (clips.Count == 1)
        {
            await PopulateSingleClipPayloadAsync(transfer, clips[0], storageProvider);
            return transfer;
        }

        await PopulateMultiClipPayloadAsync(transfer, clips, storageProvider);
        return transfer;
    }

    public Task<IReadOnlyList<ClipCaptureRequest>> TryBuildCaptureRequestsAsync(IDataTransfer drop, ClipboardSourceApplicationInfo? sourceInfo)
    {
        ArgumentNullException.ThrowIfNull(drop);

        var requests = new List<ClipCaptureRequest>();

        // 1) File drops. A single image file under the size cap is promoted to
        // an Image clip; everything else stays as a Files clip carrying paths.
        var files = drop.TryGetFiles() ?? [];
        if (files.Length > 0)
        {
            var paths = files
                .Select(static file => file.TryGetLocalPath())
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();

            if (paths.Length > 0)
            {
                if (paths.Length == 1 && IsImageExtension(Path.GetExtension(paths[0])))
                {
                    var imageRequest = TryBuildImageRequestFromFile(paths[0], sourceInfo);
                    if (imageRequest is not null)
                    {
                        requests.Add(imageRequest);
                        return Task.FromResult<IReadOnlyList<ClipCaptureRequest>>(requests);
                    }
                }

                requests.Add(BuildFilesRequest(paths, sourceInfo));
                return Task.FromResult<IReadOnlyList<ClipCaptureRequest>>(requests);
            }
        }

        // 2) Inline PNG bytes (some apps put PNG on the drag object directly).
        var pngBytes = drop.TryGetValue(PngBytesFormat);
        if (pngBytes is { Length: > 0 })
        {
            var imageRequest = TryBuildImageRequestFromBytes(pngBytes, sourceInfo);
            if (imageRequest is not null)
            {
                requests.Add(imageRequest);
                return Task.FromResult<IReadOnlyList<ClipCaptureRequest>>(requests);
            }
        }

        // 3) Inline bitmap (Avalonia coerces several image formats into Bitmap).
        var bitmap = drop.TryGetBitmap();
        if (bitmap is not null)
        {
            var imageRequest = TryBuildImageRequestFromBitmap(bitmap, sourceInfo);
            if (imageRequest is not null)
            {
                requests.Add(imageRequest);
                return Task.FromResult<IReadOnlyList<ClipCaptureRequest>>(requests);
            }
        }

        // 4) Rich text — prefer HTML, fall back to RTF.
        var plainText = drop.TryGetText();
        var html = drop.TryGetValue(CfHtmlFormat);
        if (!string.IsNullOrWhiteSpace(html))
        {
            requests.Add(BuildRichTextRequest(html, plainText, ClipContentFormat.Html, sourceInfo));
            return Task.FromResult<IReadOnlyList<ClipCaptureRequest>>(requests);
        }

        var rtf = drop.TryGetValue(RtfFormat);
        if (!string.IsNullOrWhiteSpace(rtf))
        {
            requests.Add(BuildRichTextRequest(rtf, plainText, ClipContentFormat.Rtf, sourceInfo));
            return Task.FromResult<IReadOnlyList<ClipCaptureRequest>>(requests);
        }

        // 5) Plain text fallback.
        if (!string.IsNullOrWhiteSpace(plainText))
        {
            requests.Add(BuildTextRequest(plainText, sourceInfo));
        }

        return Task.FromResult<IReadOnlyList<ClipCaptureRequest>>(requests);
    }

    private async Task PopulateSingleClipPayloadAsync(DataTransfer transfer, ClipEntry clip, IStorageProvider storageProvider)
    {
        var item = new DataTransferItem();
        switch (clip.ContentType)
        {
            case ContentType.Text:
                item.SetText(clip.Content ?? string.Empty);
                transfer.Add(item);
                break;

            case ContentType.RichText:
                var richText = clip.Content ?? string.Empty;
                item.SetText(richText);
                var richBody = clip.ContentBytes is { Length: > 0 } ? Encoding.UTF8.GetString(clip.ContentBytes) : richText;
                if (clip.ContentFormat == ClipContentFormat.Html)
                {
                    var wrapped = RichClipboardFormatting.LooksLikeCfHtml(richBody) ? richBody : RichClipboardFormatting.BuildCfHtml(richBody);
                    item.Set(CfHtmlFormat, wrapped);
                    foreach (var alt in HtmlFormatNames.Skip(1))
                    {
                        item.Set(DataFormat.CreateStringPlatformFormat(alt), wrapped);
                    }
                }
                else if (clip.ContentFormat == ClipContentFormat.Rtf)
                {
                    var normalized = RichClipboardFormatting.NormalizeRtfForClipboard(richBody);
                    item.Set(RtfFormat, normalized);
                    foreach (var alt in RtfFormatNames.Skip(1))
                    {
                        item.Set(DataFormat.CreateStringPlatformFormat(alt), normalized);
                    }
                }
                transfer.Add(item);
                break;

            case ContentType.Image:
                if (clip.ContentBytes is { Length: > 0 } imageBytes)
                {
                    var tempFile = await WriteImageTempFileAsync(clip.Id, imageBytes);
                    if (tempFile is not null)
                    {
                        var storageItem = await TryGetStorageFileAsync(storageProvider, tempFile);
                        if (storageItem is not null)
                        {
                            item.SetFile(storageItem);
                        }
                    }
                    item.Set(PngBytesFormat, imageBytes);
                    if (!string.IsNullOrWhiteSpace(clip.Content))
                    {
                        item.SetText(clip.Content);
                    }
                    transfer.Add(item);
                }
                break;

            case ContentType.Files:
                var paths = SplitFilePaths(clip.Content);
                if (paths.Length > 0)
                {
                    var resolved = await ResolveStorageItemsAsync(storageProvider, paths);
                    if (resolved.Count > 0)
                    {
                        // One DataTransferItem per file lets the target (e.g.
                        // Explorer) treat them as a multi-file drop.
                        foreach (var storageItem in resolved)
                        {
                            transfer.Add(DataTransferItem.CreateFile(storageItem));
                        }
                    }
                    var textItem = new DataTransferItem();
                    textItem.SetText(string.Join(Environment.NewLine, paths));
                    transfer.Add(textItem);
                }
                break;
        }
    }

    private async Task PopulateMultiClipPayloadAsync(DataTransfer transfer, IReadOnlyList<ClipEntry> clips, IStorageProvider storageProvider)
    {
        var textLines = new List<string>(capacity: clips.Count);

        foreach (var clip in clips)
        {
            switch (clip.ContentType)
            {
                case ContentType.Text:
                case ContentType.RichText:
                    if (!string.IsNullOrEmpty(clip.Content))
                    {
                        textLines.Add(clip.Content);
                    }
                    break;

                case ContentType.Image:
                    if (clip.ContentBytes is { Length: > 0 } bytes)
                    {
                        var tempPath = await WriteImageTempFileAsync(clip.Id, bytes);
                        if (tempPath is not null)
                        {
                            var storageItem = await TryGetStorageFileAsync(storageProvider, tempPath);
                            if (storageItem is not null)
                            {
                                transfer.Add(DataTransferItem.CreateFile(storageItem));
                            }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(clip.Content))
                    {
                        textLines.Add(clip.Content);
                    }
                    break;

                case ContentType.Files:
                    var paths = SplitFilePaths(clip.Content);
                    if (paths.Length > 0)
                    {
                        var resolved = await ResolveStorageItemsAsync(storageProvider, paths);
                        foreach (var storageItem in resolved)
                        {
                            transfer.Add(DataTransferItem.CreateFile(storageItem));
                        }
                        textLines.AddRange(paths);
                    }
                    break;
            }
        }

        if (textLines.Count > 0)
        {
            var textItem = new DataTransferItem();
            textItem.SetText(string.Join(Environment.NewLine, textLines));
            transfer.Add(textItem);
        }
    }

    private static ClipCaptureRequest BuildTextRequest(string text, ClipboardSourceApplicationInfo? sourceInfo)
        => new()
        {
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = sourceInfo?.Name,
            SourceAppPath = sourceInfo?.Path,
            SourceAppIconBytes = sourceInfo?.IconBytes,
            SourceWindowTitle = sourceInfo?.WindowTitle,
            ImportKind = ClipImportKinds.DragDrop,
        };

    private static ClipCaptureRequest BuildRichTextRequest(string richContent, string? plainText, ClipContentFormat format, ClipboardSourceApplicationInfo? sourceInfo)
    {
        var effectiveText = !string.IsNullOrWhiteSpace(plainText)
            ? plainText
            : Presentation.ClipDisplayFormatter.RenderRichContent(richContent);
        return new ClipCaptureRequest
        {
            ContentText = effectiveText,
            ContentBytes = Encoding.UTF8.GetBytes(richContent),
            ContentType = ContentType.RichText,
            ContentFormat = format,
            SourceApp = sourceInfo?.Name,
            SourceAppPath = sourceInfo?.Path,
            SourceAppIconBytes = sourceInfo?.IconBytes,
            SourceWindowTitle = sourceInfo?.WindowTitle,
            ImportKind = ClipImportKinds.DragDrop,
        };
    }

    private static ClipCaptureRequest BuildFilesRequest(string[] paths, ClipboardSourceApplicationInfo? sourceInfo)
    {
        var content = string.Join(Environment.NewLine, paths);
        return new ClipCaptureRequest
        {
            ContentText = content,
            ContentBytes = Encoding.UTF8.GetBytes(content),
            ContentType = ContentType.Files,
            ContentFormat = ClipContentFormat.FileList,
            SourceApp = sourceInfo?.Name,
            SourceAppPath = sourceInfo?.Path,
            SourceAppIconBytes = sourceInfo?.IconBytes,
            SourceWindowTitle = sourceInfo?.WindowTitle,
            ImportKind = ClipImportKinds.DragDrop,
        };
    }

    private static ClipCaptureRequest? TryBuildImageRequestFromFile(string path, ClipboardSourceApplicationInfo? sourceInfo)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return TryBuildImageRequestFromBytes(bytes, sourceInfo, Path.GetFileNameWithoutExtension(path));
        }
        catch (IOException ex)
        {
            Trace.TraceWarning($"Drag-in image read failed for {path}: {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning($"Drag-in image access denied for {path}: {ex.Message}");
            return null;
        }
    }

    private static ClipCaptureRequest? TryBuildImageRequestFromBytes(byte[] bytes, ClipboardSourceApplicationInfo? sourceInfo, string? label = null)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
            return new ClipCaptureRequest
            {
                ContentType = ContentType.Image,
                ContentFormat = ClipContentFormat.Bitmap,
                ContentText = label,
                ContentBytes = bytes,
                ImageWidth = bitmap.PixelSize.Width,
                ImageHeight = bitmap.PixelSize.Height,
                SourceApp = sourceInfo?.Name,
                SourceAppPath = sourceInfo?.Path,
                SourceAppIconBytes = sourceInfo?.IconBytes,
                SourceWindowTitle = sourceInfo?.WindowTitle,
                ImportKind = ClipImportKinds.DragDrop,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Trace.TraceWarning($"Drag-in image decode failed: {ex.Message}");
            return null;
        }
    }

    private static ClipCaptureRequest? TryBuildImageRequestFromBitmap(Avalonia.Media.Imaging.Bitmap bitmap, ClipboardSourceApplicationInfo? sourceInfo)
    {
        try
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return new ClipCaptureRequest
            {
                ContentType = ContentType.Image,
                ContentFormat = ClipContentFormat.Bitmap,
                ContentBytes = stream.ToArray(),
                ImageWidth = bitmap.PixelSize.Width,
                ImageHeight = bitmap.PixelSize.Height,
                SourceApp = sourceInfo?.Name,
                SourceAppPath = sourceInfo?.Path,
                SourceAppIconBytes = sourceInfo?.IconBytes,
                SourceWindowTitle = sourceInfo?.WindowTitle,
                ImportKind = ClipImportKinds.DragDrop,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Trace.TraceWarning($"Drag-in bitmap encode failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> WriteImageTempFileAsync(long clipId, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(DragTempDirectory);
            var path = Path.Combine(DragTempDirectory, $"clip-{clipId}.png");
            // Rewrite each drag so a stale file from a prior renamed clip
            // doesn't surface old content.
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (IOException ex)
        {
            Trace.TraceWarning($"Drag-out image temp file write failed for clip {clipId}: {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning($"Drag-out image temp file access denied for clip {clipId}: {ex.Message}");
            return null;
        }
    }

    private static async Task<IStorageFile?> TryGetStorageFileAsync(IStorageProvider provider, string path)
    {
        try
        {
            return await provider.TryGetFileFromPathAsync(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            Trace.TraceWarning($"Drag-out file resolve failed for {path}: {ex.Message}");
            return null;
        }
    }

    private static async Task<List<IStorageItem>> ResolveStorageItemsAsync(IStorageProvider provider, IReadOnlyList<string> paths)
    {
        var results = new List<IStorageItem>(paths.Count);
        foreach (var path in paths)
        {
            IStorageItem? item = null;
            try
            {
                if (Directory.Exists(path))
                {
                    item = await provider.TryGetFolderFromPathAsync(path);
                }
                else
                {
                    item = await provider.TryGetFileFromPathAsync(path);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
            {
                Trace.TraceWarning($"Drag-out path resolve failed for {path}: {ex.Message}");
            }

            if (item is not null) results.Add(item);
        }
        return results;
    }

    private static string[] SplitFilePaths(string? content)
        => string.IsNullOrWhiteSpace(content)
            ? []
            : content
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

    private static bool IsImageExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase);
    }
}
