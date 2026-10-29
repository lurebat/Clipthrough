using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Presentation;

namespace Clipthrough.Services;

/// <remarks>
/// Open in editor and Compare used to write through <see cref="ExportAsync"/>
/// too: a plaintext copy of the clip - sensitive ones included - plus a
/// metadata.json carrying the raw content, left in Documents, which is
/// usually OneDrive-synced, for good (F-18). That copy bypassed database
/// encryption and the sensitive-clip lifetime. Tool copies now go to a
/// per-session temp folder that is removed at exit, and at the next start if
/// the app did not exit cleanly.
/// </remarks>
public sealed class ClipExportService : IClipExportService, IDisposable
{
    private const string SessionFolderPrefix = "Clipthrough-tool-copies-";
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _exportRoot;
    private readonly string _sessionTempParent;
    private readonly string _sessionRoot;

    public ClipExportService()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clipthrough Exports"),
            Path.GetTempPath())
    {
    }

    /// <summary>Test seam: where exports and tool copies are written.</summary>
    internal ClipExportService(string exportRoot, string sessionTempParent)
    {
        _exportRoot = exportRoot;
        _sessionTempParent = sessionTempParent;
        _sessionRoot = Path.Combine(sessionTempParent, $"{SessionFolderPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
        RemoveAbandonedSessionFolders();
    }

    /// <summary>The folder this session's tool copies are written under.</summary>
    internal string SessionRoot => _sessionRoot;

    public Task<ClipExportResult> ExportAsync(ClipEntry clip, CancellationToken cancellationToken = default)
        => ExportCoreAsync(clip, _exportRoot, includeMetadata: true, cancellationToken);

    public Task<ClipExportResult> ExportForToolAsync(ClipEntry clip, CancellationToken cancellationToken = default)
        => ExportCoreAsync(clip, _sessionRoot, includeMetadata: false, cancellationToken);

    public void Dispose()
    {
        TryDeleteDirectory(_sessionRoot);
    }

    /// <summary>
    /// Removes tool-copy folders left by a session that did not exit cleanly.
    /// Only folders this service names, and only when the process that made
    /// them is gone - a second running instance keeps its own.
    /// </summary>
    private void RemoveAbandonedSessionFolders()
    {
        try
        {
            if (!Directory.Exists(_sessionTempParent))
            {
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(_sessionTempParent, SessionFolderPrefix + "*"))
            {
                var name = Path.GetFileName(folder);
                var pidText = name[SessionFolderPrefix.Length..].Split('-')[0];
                if (int.TryParse(pidText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)
                    && !IsProcessRunning(pid))
                {
                    TryDeleteDirectory(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not sweep abandoned tool-copy folders: {ex.Message}");
        }
    }

    private static bool IsProcessRunning(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An editor may still hold a file open; the next start sweeps it.
            System.Diagnostics.Trace.TraceWarning($"Could not remove tool-copy folder '{path}': {ex.Message}");
        }
    }

    private static async Task<ClipExportResult> ExportCoreAsync(ClipEntry clip, string exportRoot, bool includeMetadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var clipName = BuildClipFileName(clip);
        var folderName = $"{SanitizePathSegment(clipName)}-{clip.Id}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var exportDirectory = Path.Combine(exportRoot, folderName);
        Directory.CreateDirectory(exportDirectory);

        var originalPath = await ExportOriginalPayloadAsync(exportDirectory, clip, clipName, cancellationToken);
        var renderedPath = await ExportRenderedTextAsync(exportDirectory, clip, clipName, cancellationToken);
        if (includeMetadata)
        {
            await ExportMetadataAsync(exportDirectory, clip, cancellationToken);
        }

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
