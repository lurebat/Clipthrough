using System;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Presentation;

namespace Clipthrough.Services;

/// <summary>
/// Puts a clip on the clipboard in the format it was captured in, and reports
/// whether anything was actually written.
/// </summary>
/// <remarks>
/// The global paste hotkeys used to copy <c>clip.Content</c> as plain text
/// whatever the clip was. For an image that is the short text label the capture
/// stored beside the bytes, so Ctrl+V produced something like "Image 1920x1080"
/// instead of the picture - and <c>PasteAndDelete</c> then deleted the original,
/// which is the only copy. Rich text lost its formatting the same way.
///
/// The return value is the other half. A caller that deletes, favourites or
/// marks a clip pasted must only do so if the clip really reached the clipboard;
/// otherwise the keystroke pastes whatever the user had there before and the
/// clip is destroyed anyway. Every failure path here returns false without
/// writing, so "false" always means the clipboard is untouched.
///
/// Suppression is armed immediately before each write rather than once up
/// front. The gate is one-shot: arming it and then failing hands the skip to
/// whatever the user copies next, which is then missing from their history.
/// </remarks>
public static class ClipClipboardWriter
{
    /// <summary>
    /// Writes <paramref name="clip"/> to the clipboard in its captured format.
    /// </summary>
    /// <param name="maxImageBytes">
    /// Size ceiling for decoding an image clip, matching the ceiling the preview
    /// pane uses. An image above it is refused rather than decoded.
    /// </param>
    /// <returns>
    /// True when the clipboard now holds the clip. False when nothing was
    /// written, in which case the clipboard still holds whatever it held before
    /// and no follow-up action may treat the clip as pasted.
    /// </returns>
    /// <exception cref="Exception">
    /// A write that throws propagates, which is a third outcome distinct from
    /// false: the clipboard may or may not have been touched. The suppression
    /// armed for that write is withdrawn before the exception leaves, so it
    /// cannot be spent on the user's next copy.
    /// </exception>
    public static async Task<bool> TryCopyAsync(
        ClipEntry clip,
        ISystemInteractionService interaction,
        IClipboardMonitorService monitor,
        int? maxImageBytes)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(monitor);

        if (clip.ContentType == ContentType.Image)
        {
            using var bitmap = ClipBitmapFactory.TryLoad(SizeLimitedImageBytes(clip, maxImageBytes));
            if (bitmap is null)
            {
                return false;
            }

            await WriteAsync(monitor, () => interaction.CopyBitmapAsync(bitmap));
            return true;
        }

        if (string.IsNullOrEmpty(clip.Content))
        {
            return false;
        }

        if (clip.ContentType == ContentType.RichText)
        {
            var plainText = ClipDisplayFormatter.RenderRichContent(clip.Content);
            await WriteAsync(monitor, () => interaction.CopyRichContentAsync(clip.Content, plainText, clip.ContentFormat));
            return true;
        }

        // Text and file lists both go as text: a file clip's content is already
        // the newline-separated paths, and there is no file-drop write on
        // ISystemInteractionService to offer instead.
        await WriteAsync(monitor, () => interaction.CopyTextAsync(clip.Content));
        return true;
    }

    /// <summary>
    /// Arms suppression, performs the write, and withdraws the suppression if the
    /// write threw.
    /// </summary>
    /// <remarks>
    /// Arming has to happen before the write, not after: the change notification
    /// can arrive while the write is still in progress, and a suppression armed
    /// afterwards would lose that race and capture our own output. That ordering
    /// is what leaves a failed write holding an armed suppression, so the failure
    /// is unwound here rather than left to the gate's expiry window.
    /// </remarks>
    private static async Task WriteAsync(IClipboardMonitorService monitor, Func<Task> write)
    {
        monitor.SuppressNext();
        try
        {
            await write();
        }
        catch
        {
            monitor.CancelSuppressNext();
            throw;
        }
    }

    private static byte[]? SizeLimitedImageBytes(ClipEntry clip, int? maxImageBytes)
    {
        if (clip.ContentBytes is not { Length: > 0 } bytes)
        {
            return null;
        }

        return maxImageBytes is { } limit && bytes.Length > limit ? null : bytes;
    }
}
