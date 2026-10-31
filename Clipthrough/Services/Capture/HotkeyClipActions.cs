using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

/// <summary>
/// The clip-handling half of the global step-paste and custom-transform
/// hotkeys: which clip, whether it qualifies, and what reaches the clipboard.
/// Window handling and the paste keystroke stay with the caller in App, so
/// this can be tested without an application instance.
/// </summary>
internal static class HotkeyClipActions
{
    internal enum StepPasteOutcome
    {
        /// <summary>No clip at that offset; the caller steps back.</summary>
        NoClip,

        /// <summary>The clip did not reach the clipboard; nothing may be pasted.</summary>
        NotCopied,

        /// <summary>The clip is on the clipboard and marked pasted.</summary>
        Copied,
    }

    /// <summary>
    /// Puts the clip at <paramref name="offset"/> on the clipboard for the
    /// incremental / decremental paste hotkeys.
    /// </summary>
    public static async Task<StepPasteOutcome> CopyClipAtOffsetAsync(
        int offset,
        IClipStoreService store,
        ISystemInteractionService interaction,
        IClipboardMonitorService monitor,
        int? maxImageBytes)
    {
        var clip = await store.GetClipAtOffsetAsync(offset);
        if (clip is null)
        {
            return StepPasteOutcome.NoClip;
        }

        // Through the shared writer, in the clip's captured format. This used
        // to copy clip.Content as text whatever the clip was: for an image that
        // is the source window's title, which was pasted and the image marked
        // pasted (F-12). The writer arms suppression itself, per write, and
        // reports whether anything reached the clipboard.
        if (!await ClipClipboardWriter.TryCopyAsync(clip, interaction, monitor, maxImageBytes))
        {
            return StepPasteOutcome.NotCopied;
        }

        await store.MarkPastedAsync(clip.Id);
        return StepPasteOutcome.Copied;
    }

    /// <summary>
    /// Runs a custom hotkey's transform on <paramref name="clip"/>, stores the
    /// result as a new clip and puts it on the clipboard.
    /// </summary>
    /// <returns>True when the result is on the clipboard, so a paste may follow.</returns>
    public static async Task<bool> TransformAndCopyAsync(
        ClipEntry clip,
        CustomHotkeyTarget target,
        string gesture,
        IClipStoreService store,
        ISystemInteractionService interaction,
        IClipboardMonitorService monitor,
        IAiTransformService? ai,
        IReadOnlyList<AiPreset> presets)
    {
        // The same eligibility the row menu applies. Without it an image clip's
        // window title was transformed, put on the clipboard over the image,
        // and pasted into the user's document (F-12).
        if (!TextTransformationService.CanTransform(clip))
        {
            Trace.TraceInformation($"Custom hotkey '{gesture}': clip {clip.Id} is a {clip.ContentType} clip, which text transforms do not apply to.");
            return false;
        }

        var name = target.Value;
        var input = clip.Content;
        string output;
        var isHtmlOutput = false;

        switch (target.Kind)
        {
            case CustomHotkeyKind.BuiltIn:
                if (!Enum.TryParse<TextTransformation>(name, ignoreCase: true, out var tx) || tx == TextTransformation.None)
                {
                    Trace.TraceWarning($"Custom hotkey '{gesture}': unknown builtin transform '{name}'");
                    return false;
                }

                // A list row carries no markup; HTML -> Markdown on a rich clip reads it.
                if (tx is TextTransformation.HtmlToMarkdown && clip.ContentFormat is ClipContentFormat.Html && clip.ContentBytes is null)
                {
                    clip = await store.GetByIdAsync(clip.Id) ?? clip;
                }

                input = TextTransformationService.MarkupSource(tx, clip) ?? input;
                if (TextTransformationService.RefusalReason(tx, input) is { } refusal)
                {
                    Trace.TraceWarning($"Custom hotkey '{gesture}': {refusal}");
                    return false;
                }

                output = await TextTransformationService.ApplyAsync(tx, input);
                isHtmlOutput = TextTransformationService.ProducesHtml(tx);
                break;
            case CustomHotkeyKind.AiPreset:
            {
                if (ai is null || !ai.IsConfigured) return false;
                var preset = presets.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (preset is null)
                {
                    Trace.TraceWarning($"Custom hotkey '{gesture}': AI preset '{name}' not found");
                    return false;
                }
                output = await ai.TransformAsync(preset.Prompt, input);
                break;
            }
            case CustomHotkeyKind.InlinePrompt:
            {
                if (ai is null || !ai.IsConfigured) return false;
                if (string.IsNullOrWhiteSpace(name)) return false;
                output = await ai.TransformAsync(name, input);
                break;
            }
            default:
                Trace.TraceWarning($"Custom hotkey '{gesture}': unknown target kind '{target.Token}'");
                return false;
        }

        if (string.IsNullOrEmpty(output)) return false;

        // Persist the transformed result as a new clip
        var outputBytes = System.Text.Encoding.UTF8.GetBytes(output);
        await store.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = outputBytes,
            ContentText = output,
            ContentType = isHtmlOutput ? ContentType.RichText : ContentType.Text,
            ContentFormat = isHtmlOutput ? ClipContentFormat.Html : ClipContentFormat.PlainText,
            SourceApp = clip.SourceApp,
            SourceAppPath = clip.SourceAppPath,
            SourceAppIconBytes = clip.SourceAppIconBytes,
            SourceWindowTitle = clip.SourceWindowTitle,
            IncrementExistingCopyCount = false,
            SourceClipId = clip.Id,
            TransformKind = $"{target.Token}:{name}",
            SkipPostInsertMaintenance = true,
        });

        if (isHtmlOutput)
        {
            var plain = Presentation.ClipDisplayFormatter.RenderRichContent(output);
            monitor.SuppressNext(plain);
            await interaction.CopyRichContentAsync(output, plain, ClipContentFormat.Html);
        }
        else
        {
            monitor.SuppressNext(output);
            await interaction.CopyTextAsync(output);
        }

        return true;
    }
}
