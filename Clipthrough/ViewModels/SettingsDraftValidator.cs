using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia.Input;

using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Services;

namespace Clipthrough.ViewModels;

/// <summary>
/// The hotkeys and numeric limits from a settings draft, normalized and known
/// good. Produced only by <see cref="SettingsDraftValidator.TryValidate"/>, so
/// holding one is evidence the draft passed.
/// </summary>
internal sealed record ValidatedSettingsDraft(
    IReadOnlyDictionary<string, string> LocalHotkeys,
    string GlobalHotkey,
    string IncrementalPasteHotkey,
    string DecrementalPasteHotkey,
    IReadOnlyDictionary<string, string> ExtendedHotkeys,
    int MaxClipSizeBytes,
    int NormalClipLifetimeDays,
    int SensitiveClipLifetimeMinutes,
    int MaxLibrarySizeMegabytes,
    int MaxEntryCount);

/// <summary>
/// Checks a settings draft before anything is written.
/// </summary>
/// <remarks>
/// Split out of <c>MainWindowViewModel.SaveSettingsAsync</c>, which was 359
/// lines of which the first 155 were this. Two reasons beyond the size.
///
/// It is the half with no side effects, so separating it makes the remaining
/// half - which moves databases and rewrites credentials - short enough to read
/// as the transaction it is.
///
/// And it was only reachable by running a save. Every rule here is a refusal
/// the user can hit (a hotkey that will not parse, one claimed twice, one the
/// window already answers, a size outside its bounds), and none of them could
/// be exercised without also exercising storage.
/// </remarks>
internal static class SettingsDraftValidator
{
    private readonly record struct HotkeyDraft(string Name, bool IsEnabled, string HotkeyText);

    /// <summary>
    /// Normalizes every hotkey and limit on <paramref name="draft"/>.
    /// </summary>
    /// <param name="error">
    /// A message ready for the status bar, already formatted. Null on success.
    /// </param>
    /// <returns>True when the draft is safe to apply.</returns>
    internal static bool TryValidate(
        SettingsViewModel draft,
        out ValidatedSettingsDraft? validated,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(draft);

        validated = null;
        error = null;

        var localDrafts = new[]
        {
            new HotkeyDraft(nameof(AppSettings.ToggleRegexHotkey), draft.EnableToggleRegexHotkey, draft.ToggleRegexHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleFavoritesHotkey), draft.EnableToggleFavoritesHotkey, draft.ToggleFavoritesHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleSensitiveHotkey), draft.EnableToggleSensitiveHotkey, draft.ToggleSensitiveHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleCaseSensitiveHotkey), draft.EnableToggleCaseSensitiveHotkey, draft.ToggleCaseSensitiveHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleWildcardHotkey), draft.EnableToggleWildcardHotkey, draft.ToggleWildcardHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleWholeWordHotkey), draft.EnableToggleWholeWordHotkey, draft.ToggleWholeWordHotkey),
            new HotkeyDraft(nameof(AppSettings.TogglePastedHotkey), draft.EnableTogglePastedHotkey, draft.TogglePastedHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleFuzzyHotkey), draft.EnableToggleFuzzyHotkey, draft.ToggleFuzzyHotkey),
            new HotkeyDraft(nameof(AppSettings.ToggleSemanticHotkey), draft.EnableToggleSemanticHotkey, draft.ToggleSemanticHotkey),
        };

        var localHotkeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in localDrafts)
        {
            if (!pair.IsEnabled)
            {
                localHotkeys[pair.Name] = pair.HotkeyText.Trim();
                continue;
            }

            if (!TryParseAvaloniaGesture(pair.HotkeyText, out var gesture) || gesture is null)
            {
                error = AppText.FormatSettingsValidationError(AppText.SettingsInvalidHotkeyFallback);
                return false;
            }

            localHotkeys[pair.Name] = gesture.ToString();
        }

        if (!TryNormalizeGlobal(draft.EnableToggleWindowHotkey, draft.ToggleWindowHotkey, out var globalHotkey, out error)
            || !TryNormalizeGlobal(draft.EnableIncrementalPasteHotkey, draft.IncrementalPasteHotkey, out var incrementalHotkey, out error)
            || !TryNormalizeGlobal(draft.EnableDecrementalPasteHotkey, draft.DecrementalPasteHotkey, out var decrementalHotkey, out error))
        {
            return false;
        }

        var extendedDrafts = new[]
        {
            ("copy-and-favorite", draft.EnableCopyAndFavoriteHotkey, draft.CopyAndFavoriteHotkey),
            ("copy-and-sensitive", draft.EnableCopyAndSensitiveHotkey, draft.CopyAndSensitiveHotkey),
            ("copy-without-saving", draft.EnableCopyWithoutSavingHotkey, draft.CopyWithoutSavingHotkey),
            ("paste-and-delete", draft.EnablePasteAndDeleteHotkey, draft.PasteAndDeleteHotkey),
            ("paste-and-favorite", draft.EnablePasteAndFavoriteHotkey, draft.PasteAndFavoriteHotkey),
            ("paste-as-plain-text", draft.EnablePasteAsPlainTextHotkey, draft.PasteAsPlainTextHotkey),
        };

        var extendedHotkeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, enabled, raw) in extendedDrafts)
        {
            var normalized = (raw ?? string.Empty).Trim();
            if (enabled)
            {
                if (!HotkeyGesture.TryParse(normalized, out var parsed, out var parseError) || parsed is null)
                {
                    error = AppText.FormatSettingsValidationError(parseError ?? AppText.SettingsInvalidHotkeyFallback);
                    return false;
                }

                normalized = parsed.ToString();
            }

            extendedHotkeys[id] = normalized;
        }

        var duplicate = localDrafts
            .Where(static d => d.IsEnabled)
            .Select(d => localHotkeys[d.Name])
            .Append(draft.EnableToggleWindowHotkey ? globalHotkey : string.Empty)
            .Append(draft.EnableIncrementalPasteHotkey ? incrementalHotkey : string.Empty)
            .Append(draft.EnableDecrementalPasteHotkey ? decrementalHotkey : string.Empty)
            .Concat(extendedDrafts.Where(h => h.Item2).Select(h => extendedHotkeys[h.Item1]))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            error = AppText.FormatSettingsValidationError(AppText.FormatDuplicateHotkey(duplicate.Key));
            return false;
        }

        // The window's own handlers run before TryHandleShortcut, so a filter
        // hotkey that matches one of them never fires while that built-in
        // applies - and for the clip-list built-ins that is the window's normal
        // focus state. Refuse the assignment rather than let the user configure
        // a shortcut that silently does nothing.
        foreach (var pair in localDrafts.Where(static d => d.IsEnabled))
        {
            var normalized = localHotkeys[pair.Name];
            if (BuiltInShortcuts.DescribeCollision(normalized) is { } builtIn)
            {
                error = AppText.FormatSettingsValidationError(AppText.FormatHotkeyReservedByBuiltIn(normalized, builtIn));
                return false;
            }
        }

        if (!TryParseMaxClipSizeBytes(draft.MaxClipSizeKilobytes, out var maxClipSizeBytes))
        {
            error = AppText.FormatSettingsValidationError(AppText.SettingsInvalidClipSize);
            return false;
        }

        if (!TryParseOptionalPositiveInt(draft.EnableNormalClipLifetime, draft.NormalClipLifetimeDays, AppSettings.MinNormalClipLifetimeDays, AppSettings.MaxNormalClipLifetimeDays, out var normalClipLifetimeDays))
        {
            error = AppText.FormatSettingsValidationError(AppText.SettingsInvalidNormalLifetime);
            return false;
        }

        if (!TryParseOptionalPositiveInt(draft.EnableSensitiveClipLifetime, draft.SensitiveClipLifetimeMinutes, AppSettings.MinSensitiveClipLifetimeMinutes, AppSettings.MaxSensitiveClipLifetimeMinutes, out var sensitiveClipLifetimeMinutes))
        {
            error = AppText.FormatSettingsValidationError(AppText.SettingsInvalidSensitiveLifetime);
            return false;
        }

        if (!TryParseOptionalPositiveInt(draft.EnableMaxLibrarySize, draft.MaxLibrarySizeMegabytes, AppSettings.MinMaxLibrarySizeMegabytes, AppSettings.MaxMaxLibrarySizeMegabytes, out var maxLibrarySizeMegabytes))
        {
            error = AppText.FormatSettingsValidationError(AppText.SettingsInvalidMaxLibrarySize);
            return false;
        }

        if (!TryParseOptionalPositiveInt(draft.EnableMaxEntryCount, draft.MaxEntryCount, AppSettings.MinMaxEntryCount, AppSettings.MaxMaxEntryCount, out var maxEntryCount))
        {
            error = AppText.FormatSettingsValidationError(AppText.SettingsInvalidMaxEntryCount);
            return false;
        }

        validated = new ValidatedSettingsDraft(
            localHotkeys,
            globalHotkey,
            incrementalHotkey,
            decrementalHotkey,
            extendedHotkeys,
            maxClipSizeBytes,
            normalClipLifetimeDays,
            sensitiveClipLifetimeMinutes,
            maxLibrarySizeMegabytes,
            maxEntryCount);
        return true;
    }

    /// <summary>
    /// Global and paste hotkeys parse through <see cref="HotkeyGesture"/> rather
    /// than Avalonia's, because they are registered with the OS.
    /// </summary>
    private static bool TryNormalizeGlobal(bool isEnabled, string raw, out string normalized, out string? error)
    {
        error = null;
        normalized = (raw ?? string.Empty).Trim();
        if (!isEnabled)
        {
            return true;
        }

        if (!HotkeyGesture.TryParse(normalized, out var parsed, out var parseError) || parsed is null)
        {
            error = AppText.FormatSettingsValidationError(parseError ?? AppText.SettingsInvalidHotkeyFallback);
            return false;
        }

        normalized = parsed.ToString();
        return true;
    }

    private static bool TryParseAvaloniaGesture(string? value, out KeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            gesture = KeyGesture.Parse(value.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryParseOptionalPositiveInt(bool isEnabled, string? value, int min, int max, out int parsed)
    {
        parsed = min;
        if (!isEnabled)
        {
            return true;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
               && parsed >= min
               && parsed <= max;
    }

    private static bool TryParseMaxClipSizeBytes(string? value, out int maxClipSizeBytes)
    {
        maxClipSizeBytes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var kilobytes)
            && !double.TryParse(normalized, NumberStyles.Float, CultureInfo.CurrentCulture, out kilobytes))
        {
            return false;
        }

        var bytes = (int)Math.Round(kilobytes * 1024d, MidpointRounding.AwayFromZero);
        if (bytes < AppSettings.MinMaxClipSizeBytes || bytes > AppSettings.MaxMaxClipSizeBytes)
        {
            return false;
        }

        maxClipSizeBytes = bytes;
        return true;
    }
}
