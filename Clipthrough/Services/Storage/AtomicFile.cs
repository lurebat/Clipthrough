using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

/// <summary>
/// Writes small configuration files so that a crash, a power loss or a full
/// disk part-way through leaves either the old file or the new one, never a
/// torn mix: the content goes to a sibling temp file, is flushed to disk, and
/// is then renamed over the target.
/// </summary>
/// <remarks>
/// A direct <see cref="File.WriteAllText(string, string?)"/> truncates the
/// target first, so dying mid-write leaves a half file. For storage.json that
/// silently re-pointed the app at an empty default library (F-22); for
/// settings.json it silently fell back to a legacy mirror. Every writer of a
/// file Clipthrough reads back at startup goes through here, so they cannot
/// drift apart again.
/// </remarks>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
        => WriteAllBytesAsync(path, Utf8NoBom.GetBytes(contents), cancellationToken);

    public static void WriteAllText(string path, string contents)
        => WriteAllBytes(path, Utf8NoBom.GetBytes(contents));

    public static async Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
    {
        var tempPath = TempPathFor(path);
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void WriteAllBytes(string path, byte[] contents)
    {
        var tempPath = TempPathFor(path);
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Renames an unreadable file out of the way, so the next save cannot
    /// overwrite the only evidence of what was in it, and returns the new
    /// path. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the rename fails.
    /// </summary>
    public static string Quarantine(string path)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var quarantinePath = $"{path}.corrupt-{stamp}";
        File.Move(path, quarantinePath, overwrite: true);
        return quarantinePath;
    }

    private static string TempPathFor(string path) => path + ".tmp";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray .tmp is overwritten by the next write.
        }
    }
}
