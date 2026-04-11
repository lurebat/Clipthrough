using System;
using System.Collections.Generic;
using System.Globalization;
using Clipthrough.Models;

namespace Clipthrough.Localization;

public static class AppText
{
    private static readonly IReadOnlyDictionary<string, string> s_en = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(WindowTitle)] = "Clipthrough",
        [nameof(SearchWatermark)] = "Filter clips",
        [nameof(ClipboardHistoryCaption)] = "Clipboard history",
        [nameof(ClipsPanelTitle)] = "Clips",
        [nameof(FavoritesFilterLabel)] = "Favorites",
        [nameof(SensitiveFilterLabel)] = "Sensitive",
        [nameof(RegexFilterLabel)] = "Regex",
        [nameof(CaseSensitiveFilterLabel)] = "Case",
        [nameof(RefreshButtonLabel)] = "Refresh",
        [nameof(OpenButtonLabel)] = "Open",
        [nameof(RawToggleLabel)] = "Raw",
        [nameof(CopyButtonLabel)] = "Copy",
        [nameof(DeleteButtonLabel)] = "Delete",
        [nameof(FolderButtonLabel)] = "Folder",
        [nameof(FavoriteButtonLabel)] = "Favorite",
        [nameof(FavoriteBadgeLabel)] = "Favorite",
        [nameof(SettingsButtonLabel)] = "Settings",
        [nameof(SettingsTitleText)] = "Settings",
        [nameof(SettingsDescriptionText)] = "Adjust keyboard shortcuts, tray behavior, startup, and the maximum stored clip size.",
        [nameof(SettingsLocalHotkeysTitle)] = "Local shortcuts",
        [nameof(SettingsGlobalHotkeyTitle)] = "Global shortcut and capture limits",
        [nameof(SettingsBehaviorTitle)] = "Window behavior",
        [nameof(SettingsClipLimitLabel)] = "Max clip size (KB)",
        [nameof(SettingsRegexHotkeyLabel)] = "Toggle regex",
        [nameof(SettingsFavoritesHotkeyLabel)] = "Toggle favorites",
        [nameof(SettingsSensitiveHotkeyLabel)] = "Toggle sensitive",
        [nameof(SettingsCaseSensitiveHotkeyLabel)] = "Toggle case sensitivity",
        [nameof(SettingsToggleWindowHotkeyLabel)] = "Toggle window",
        [nameof(SettingsCloseToTrayLabel)] = "Close to tray",
        [nameof(SettingsMinimizeToTrayLabel)] = "Minimize to tray",
        [nameof(SettingsStartWithWindowsLabel)] = "Start with Windows",
        [nameof(SettingsSaveButtonLabel)] = "Save",
        [nameof(SettingsCancelButtonLabel)] = "Cancel",
        [nameof(SettingsHintText)] = "Enter shortcuts in forms like Alt+R, Ctrl+Shift+F, or F8. The default max clip size is 2 KB. Startup registration applies on Windows.",
        [nameof(EmptySelectionTitle)] = "Select a clip",
        [nameof(EmptySelectionDescription)] = "Choose a clip from the left to inspect its content, preview it in context, and review metadata below.",
        [nameof(ImageClipTitle)] = "Image clip",
        [nameof(AppLabel)] = "App",
        [nameof(CapturedLabel)] = "Last copied",
        [nameof(FirstCopiedLabel)] = "First copied",
        [nameof(CopiesLabel)] = "Copies",
        [nameof(SizeLabel)] = "Size",
        [nameof(ResolutionLabel)] = "Resolution",
        [nameof(SensitivityLabel)] = "Sensitivity",
        [nameof(UnknownSource)] = "Unknown source",
        [nameof(NoClipSelected)] = "No clip selected",
        [nameof(NotAvailable)] = "—",
        [nameof(LoadingStatus)] = "Loading…",
        [nameof(WaitingForFirstCapture)] = "Waiting for first capture",
        [nameof(RemoveFavorite)] = "Remove Favorite",
        [nameof(AddFavorite)] = "Add Favorite",
        [nameof(SettingsSavedStatus)] = "Settings saved.",
        [nameof(SettingsInvalidHotkeyFallback)] = "Enter a valid hotkey such as Alt+R.",
        [nameof(SettingsInvalidClipSize)] = "Enter a clip size between 0.25 KB and 1024 KB.",
        [nameof(SelectedClipStateTitle)] = "Selected clip",
        [nameof(EmptySelectionStateTitle)] = "Choose a clip from the list to preview its details.",
        [nameof(ClipboardRefreshingState)] = "Refreshing clipboard library…",
        [nameof(ClipboardLoadMoreState)] = "Scroll to load more clips.",
        [nameof(ClipboardLoadedState)] = "Everything matching your filters is loaded.",
        [nameof(FilterSummaryAll)] = "Showing the full clipboard archive",
        [nameof(FilterFavorites)] = "Favorites",
        [nameof(FilterSensitive)] = "Sensitive",
        [nameof(FilterRegex)] = "Regex",
        [nameof(FilterCaseSensitive)] = "Case sensitive",
        [nameof(EmptyListRegex)] = "No clips match the current regex filters.",
        [nameof(EmptyListDefault)] = "No clips match the current filters.",
        [nameof(NoCapturesYet)] = "No captures yet",
        [nameof(NoCapturesYetLower)] = "no captures yet",
        [nameof(SelectClipTypeFallback)] = "Clip",
        [nameof(SelectClipTitleFallback)] = "Select a clip",
        [nameof(PreviewSelectContent)] = "Select a clip to preview its content.",
        [nameof(PreviewSelectRawContent)] = "Select a clip to preview its full content.",
        [nameof(PreviewSelectImage)] = "Select an image clip to preview it.",
        [nameof(PreviewImageLoaded)] = "Image preview loaded from the stored clipboard payload.",
        [nameof(PreviewImageTooLarge)] = "Image preview skipped because the stored clip is larger than the current size limit.",
        [nameof(PreviewImageTextOnly)] = "This entry is marked as an image, but the stored payload is text only. Switch to raw mode to inspect the original data.",
        [nameof(PreviewImageResolution)] = "Thumbnail preview · {0}",
        [nameof(PreviewEmptyImageData)] = "This image clip does not include previewable image data.",
        [nameof(PreviewEmptyFilesData)] = "This file clip does not include any stored paths.",
        [nameof(PreviewEmptyRichTextData)] = "This rich text clip is empty.",
        [nameof(PreviewEmptyClip)] = "This clip is empty.",
        [nameof(PreviewTextUnavailable)] = "This clip does not contain previewable text.",
        [nameof(EmptyClip)] = "Empty clip",
        [nameof(SensitivityNoMatch)] = "No sensitive patterns matched",
        [nameof(AvailabilityAvailable)] = "Available",
        [nameof(AvailabilityMissing)] = "Missing",
        [nameof(ClipboardAccessUnavailable)] = "Clipboard access is not available.",
        [nameof(ContainingDirectoryNotFound)] = "The containing directory could not be found.",
        [nameof(PathRequired)] = "A path is required.",
        [nameof(JustNow)] = "just now",
        ["ContentType.Text"] = "Text",
        ["ContentType.Image"] = "Image",
        ["ContentType.RichText"] = "Rich text",
        ["ContentType.Files"] = "Files",
        ["ContentType.All"] = "All",
        ["ClipTitle.Image"] = "Image clip",
        ["ClipTitle.Files"] = "File list clip",
        ["ClipTitle.RichText"] = "Rich text clip",
        ["ClipTitle.EmptyText"] = "Empty text clip",
        ["PreviewSnippet.Image"] = "Image data captured from the clipboard.",
        ["PreviewSnippet.Files"] = "File paths captured from the clipboard.",
        ["PreviewSnippet.RichText"] = "Formatted text content captured from the clipboard.",
        ["PreviewSnippet.SingleLineFiles"] = "File list",
        ["Format.FileCountSingular"] = "{0} file captured",
        ["Format.FileCountPlural"] = "{0} files captured",
        ["Format.ByteCount"] = "{0:N0} bytes",
        ["Format.MatchingCount"] = "Matching {0:N0}",
        ["Format.SensitiveCount"] = "Sensitive {0:N0}",
        ["Format.SearchFilter"] = "Search: \"{0}\"",
        ["Format.LastCapture"] = "Last copy {0}",
        ["Format.StatusSummary"] = "{0:N0} matching · {1:N0} total clips · {2:N0} sensitive · Last copy {3}",
        ["Format.CopyCountSingular"] = "{0:N0} copy",
        ["Format.CopyCountPlural"] = "{0:N0} copies",
        ["Format.CopyCountCompact"] = "×{0:N0}",
        ["Format.ImageDimensions"] = "{0:N0} × {1:N0}",
        ["Format.ImageSummary"] = "Image · {0}",
        ["Format.CopiedClipStatus"] = "Copied {0} clip to the clipboard.",
        ["Format.CopiedFileListStatus"] = "Copied {0} file paths to the clipboard.",
        ["Format.CopiedImageStatus"] = "Copied image clip to the clipboard.",
        ["Format.CopiedPathStatus"] = "Copied path: {0}.",
        ["Format.OpenedFileStatus"] = "Opened: {0}.",
        ["Format.OpenedContainingFolderStatus"] = "Opened containing folder for {0}.",
        ["Format.CopyFailed"] = "Copy failed: {0}",
        ["Format.OpenFailed"] = "Open failed: {0}",
        ["Format.FolderOpenFailed"] = "Folder open failed: {0}",
        ["Format.ErrorStatus"] = "Error: {0}",
        ["Format.SettingsValidationError"] = "Settings error: {0}",
        ["Format.PathNotFound"] = "The requested file or directory could not be found: {0}",
        ["Format.DuplicateHotkey"] = "The hotkey {0} is assigned more than once.",
        ["Format.RelativeMinutes"] = "{0} min ago",
        ["Format.RelativeHours"] = "{0} hr ago",
        ["Format.RelativeDaysSingular"] = "{0} day ago",
        ["Format.RelativeDaysPlural"] = "{0} days ago",
        ["Severity.info"] = "Sensitive",
        ["Severity.warning"] = "Warning",
        ["Severity.critical"] = "Critical",
        ["Format.SeverityBadge"] = "{0} severity",
        ["Format.ViewNotFound"] = "Not Found: {0}",
    };

    private static CultureInfo _currentCulture = CultureInfo.CurrentUICulture;

    public static event EventHandler? CultureChanged;

    public static CultureInfo CurrentCulture => _currentCulture;

    public static string WindowTitle => Text(nameof(WindowTitle));
    public static string SearchWatermark => Text(nameof(SearchWatermark));
    public static string ClipboardHistoryCaption => Text(nameof(ClipboardHistoryCaption));
    public static string ClipsPanelTitle => Text(nameof(ClipsPanelTitle));
    public static string FavoritesFilterLabel => Text(nameof(FavoritesFilterLabel));
    public static string SensitiveFilterLabel => Text(nameof(SensitiveFilterLabel));
    public static string RegexFilterLabel => Text(nameof(RegexFilterLabel));
    public static string CaseSensitiveFilterLabel => Text(nameof(CaseSensitiveFilterLabel));
    public static string RefreshButtonLabel => Text(nameof(RefreshButtonLabel));
    public static string OpenButtonLabel => Text(nameof(OpenButtonLabel));
    public static string RawToggleLabel => Text(nameof(RawToggleLabel));
    public static string CopyButtonLabel => Text(nameof(CopyButtonLabel));
    public static string DeleteButtonLabel => Text(nameof(DeleteButtonLabel));
    public static string FolderButtonLabel => Text(nameof(FolderButtonLabel));
    public static string FavoriteButtonLabel => Text(nameof(FavoriteButtonLabel));
    public static string FavoriteBadgeLabel => Text(nameof(FavoriteBadgeLabel));
    public static string SettingsButtonLabel => Text(nameof(SettingsButtonLabel));
    public static string SettingsTitleText => Text(nameof(SettingsTitleText));
    public static string SettingsDescriptionText => Text(nameof(SettingsDescriptionText));
    public static string SettingsLocalHotkeysTitle => Text(nameof(SettingsLocalHotkeysTitle));
    public static string SettingsGlobalHotkeyTitle => Text(nameof(SettingsGlobalHotkeyTitle));
    public static string SettingsBehaviorTitle => Text(nameof(SettingsBehaviorTitle));
    public static string SettingsClipLimitLabel => Text(nameof(SettingsClipLimitLabel));
    public static string SettingsRegexHotkeyLabel => Text(nameof(SettingsRegexHotkeyLabel));
    public static string SettingsFavoritesHotkeyLabel => Text(nameof(SettingsFavoritesHotkeyLabel));
    public static string SettingsSensitiveHotkeyLabel => Text(nameof(SettingsSensitiveHotkeyLabel));
    public static string SettingsCaseSensitiveHotkeyLabel => Text(nameof(SettingsCaseSensitiveHotkeyLabel));
    public static string SettingsToggleWindowHotkeyLabel => Text(nameof(SettingsToggleWindowHotkeyLabel));
    public static string SettingsCloseToTrayLabel => Text(nameof(SettingsCloseToTrayLabel));
    public static string SettingsMinimizeToTrayLabel => Text(nameof(SettingsMinimizeToTrayLabel));
    public static string SettingsStartWithWindowsLabel => Text(nameof(SettingsStartWithWindowsLabel));
    public static string SettingsSaveButtonLabel => Text(nameof(SettingsSaveButtonLabel));
    public static string SettingsCancelButtonLabel => Text(nameof(SettingsCancelButtonLabel));
    public static string SettingsHintText => Text(nameof(SettingsHintText));
    public static string EmptySelectionTitle => Text(nameof(EmptySelectionTitle));
    public static string EmptySelectionDescription => Text(nameof(EmptySelectionDescription));
    public static string ImageClipTitle => Text(nameof(ImageClipTitle));
    public static string AppLabel => Text(nameof(AppLabel));
    public static string CapturedLabel => Text(nameof(CapturedLabel));
    public static string FirstCopiedLabel => Text(nameof(FirstCopiedLabel));
    public static string CopiesLabel => Text(nameof(CopiesLabel));
    public static string SizeLabel => Text(nameof(SizeLabel));
    public static string ResolutionLabel => Text(nameof(ResolutionLabel));
    public static string SensitivityLabel => Text(nameof(SensitivityLabel));
    public static string UnknownSource => Text(nameof(UnknownSource));
    public static string NoClipSelected => Text(nameof(NoClipSelected));
    public static string NotAvailable => Text(nameof(NotAvailable));
    public static string LoadingStatus => Text(nameof(LoadingStatus));
    public static string WaitingForFirstCapture => Text(nameof(WaitingForFirstCapture));
    public static string RemoveFavorite => Text(nameof(RemoveFavorite));
    public static string AddFavorite => Text(nameof(AddFavorite));
    public static string SettingsSavedStatus => Text(nameof(SettingsSavedStatus));
    public static string SettingsInvalidHotkeyFallback => Text(nameof(SettingsInvalidHotkeyFallback));
    public static string SettingsInvalidClipSize => Text(nameof(SettingsInvalidClipSize));
    public static string SelectedClipStateTitle => Text(nameof(SelectedClipStateTitle));
    public static string EmptySelectionStateTitle => Text(nameof(EmptySelectionStateTitle));
    public static string ClipboardRefreshingState => Text(nameof(ClipboardRefreshingState));
    public static string ClipboardLoadMoreState => Text(nameof(ClipboardLoadMoreState));
    public static string ClipboardLoadedState => Text(nameof(ClipboardLoadedState));
    public static string FilterSummaryAll => Text(nameof(FilterSummaryAll));
    public static string FilterFavorites => Text(nameof(FilterFavorites));
    public static string FilterSensitive => Text(nameof(FilterSensitive));
    public static string FilterRegex => Text(nameof(FilterRegex));
    public static string FilterCaseSensitive => Text(nameof(FilterCaseSensitive));
    public static string EmptyListRegex => Text(nameof(EmptyListRegex));
    public static string EmptyListDefault => Text(nameof(EmptyListDefault));
    public static string NoCapturesYet => Text(nameof(NoCapturesYet));
    public static string NoCapturesYetLower => Text(nameof(NoCapturesYetLower));
    public static string SelectClipTypeFallback => Text(nameof(SelectClipTypeFallback));
    public static string SelectClipTitleFallback => Text(nameof(SelectClipTitleFallback));
    public static string PreviewSelectContent => Text(nameof(PreviewSelectContent));
    public static string PreviewSelectRawContent => Text(nameof(PreviewSelectRawContent));
    public static string PreviewSelectImage => Text(nameof(PreviewSelectImage));
    public static string PreviewImageLoaded => Text(nameof(PreviewImageLoaded));
    public static string PreviewImageTooLarge => Text(nameof(PreviewImageTooLarge));
    public static string PreviewImageTextOnly => Text(nameof(PreviewImageTextOnly));
    public static string PreviewImageResolution => Text(nameof(PreviewImageResolution));
    public static string PreviewEmptyImageData => Text(nameof(PreviewEmptyImageData));
    public static string PreviewEmptyFilesData => Text(nameof(PreviewEmptyFilesData));
    public static string PreviewEmptyRichTextData => Text(nameof(PreviewEmptyRichTextData));
    public static string PreviewEmptyClip => Text(nameof(PreviewEmptyClip));
    public static string PreviewTextUnavailable => Text(nameof(PreviewTextUnavailable));
    public static string EmptyClip => Text(nameof(EmptyClip));
    public static string SensitivityNoMatch => Text(nameof(SensitivityNoMatch));
    public static string AvailabilityAvailable => Text(nameof(AvailabilityAvailable));
    public static string AvailabilityMissing => Text(nameof(AvailabilityMissing));
    public static string ClipboardAccessUnavailable => Text(nameof(ClipboardAccessUnavailable));
    public static string ContainingDirectoryNotFound => Text(nameof(ContainingDirectoryNotFound));
    public static string PathRequired => Text(nameof(PathRequired));
    public static string JustNow => Text(nameof(JustNow));

    public static void SetCulture(CultureInfo culture)
    {
        if (culture.Equals(_currentCulture))
        {
            return;
        }

        _currentCulture = culture;
        CultureChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string GetContentTypeLabel(ContentType contentType) => contentType switch
    {
        ContentType.Text => Text("ContentType.Text"),
        ContentType.Image => Text("ContentType.Image"),
        ContentType.RichText => Text("ContentType.RichText"),
        ContentType.Files => Text("ContentType.Files"),
        _ => Text("ContentType.Text"),
    };

    public static string GetFilterContentTypeLabel(ContentType? contentType) => contentType is null
        ? Text("ContentType.All")
        : GetContentTypeLabel(contentType.Value);

    public static string GetEmptyClipTitle(ContentType contentType) => contentType switch
    {
        ContentType.Image => Text("ClipTitle.Image"),
        ContentType.Files => Text("ClipTitle.Files"),
        ContentType.RichText => Text("ClipTitle.RichText"),
        _ => Text("ClipTitle.EmptyText"),
    };

    public static string GetEmptyPreviewSnippet(ContentType contentType) => contentType switch
    {
        ContentType.Image => Text("PreviewSnippet.Image"),
        ContentType.Files => Text("PreviewSnippet.Files"),
        ContentType.RichText => Text("PreviewSnippet.RichText"),
        _ => PreviewTextUnavailable,
    };

    public static string GetEmptySingleLinePreview(ContentType contentType) => contentType switch
    {
        ContentType.Image => Text("ClipTitle.Image"),
        ContentType.Files => Text("PreviewSnippet.SingleLineFiles"),
        ContentType.RichText => Text("ClipTitle.RichText"),
        _ => Text("ClipTitle.EmptyText"),
    };

    public static string FormatFileCount(int count) => count == 1
        ? Format("Format.FileCountSingular", count)
        : Format("Format.FileCountPlural", count);

    public static string FormatByteCount(long bytes) => Format("Format.ByteCount", bytes);

    public static string FormatMatchingCount(int count) => Format("Format.MatchingCount", count);

    public static string FormatSensitiveCount(int count) => Format("Format.SensitiveCount", count);

    public static string FormatSearchFilter(string searchText) => Format("Format.SearchFilter", searchText);

    public static string FormatLastCapture(string lastCapture) => Format("Format.LastCapture", lastCapture);

    public static string FormatStatusSummary(int matchingCount, int totalClipCount, int sensitiveClipCount, string lastCapture) =>
        Format("Format.StatusSummary", matchingCount, totalClipCount, sensitiveClipCount, lastCapture);

    public static string FormatCopyCount(int count) => count == 1
        ? Format("Format.CopyCountSingular", count)
        : Format("Format.CopyCountPlural", count);

    public static string FormatCopyCountCompact(int count) => Format("Format.CopyCountCompact", count);

    public static string FormatImageDimensions(int width, int height) => Format("Format.ImageDimensions", width, height);

    public static string FormatImageSummary(string dimensions) => Format("Format.ImageSummary", dimensions);

    public static string FormatCopiedClip(string contentTypeDisplayName) => Format("Format.CopiedClipStatus", contentTypeDisplayName);

    public static string FormatCopiedFileList(int count) => Format("Format.CopiedFileListStatus", count);

    public static string CopiedImageStatus => Text("Format.CopiedImageStatus");

    public static string FormatCopiedPath(string fileName) => Format("Format.CopiedPathStatus", fileName);

    public static string FormatOpenedFile(string fileName) => Format("Format.OpenedFileStatus", fileName);

    public static string FormatOpenedContainingFolder(string fileName) => Format("Format.OpenedContainingFolderStatus", fileName);

    public static string FormatCopyFailed(string message) => Format("Format.CopyFailed", message);

    public static string FormatOpenFailed(string message) => Format("Format.OpenFailed", message);

    public static string FormatFolderOpenFailed(string message) => Format("Format.FolderOpenFailed", message);

    public static string FormatErrorStatus(string message) => Format("Format.ErrorStatus", message);

    public static string FormatSettingsValidationError(string message) => Format("Format.SettingsValidationError", message);

    public static string FormatMissingPath(string path) => Format("Format.PathNotFound", path);

    public static string FormatDuplicateHotkey(string hotkey) => Format("Format.DuplicateHotkey", hotkey);

    public static string FormatRelativeMinutes(int minutes) => Format("Format.RelativeMinutes", minutes);

    public static string FormatRelativeHours(int hours) => Format("Format.RelativeHours", hours);

    public static string FormatRelativeDays(int days) => days == 1
        ? Format("Format.RelativeDaysSingular", days)
        : Format("Format.RelativeDaysPlural", days);

    public static string GetSeverityLabel(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => Text("Severity.critical"),
        "warning" => Text("Severity.warning"),
        "info" => Text("Severity.info"),
        _ => Text("Severity.info"),
    };

    public static string GetSeverityBadgeLabel(string? severity) => Format("Format.SeverityBadge", GetSeverityLabel(severity));

    public static string FormatViewNotFound(string name) => Format("Format.ViewNotFound", name);

    private static string Text(string key)
    {
        var catalog = GetCatalog();
        return catalog.TryGetValue(key, out var value) ? value : key;
    }

    private static string Format(string key, params object?[] arguments) => string.Format(CurrentCulture, Text(key), arguments);

    private static IReadOnlyDictionary<string, string> GetCatalog() => _currentCulture.TwoLetterISOLanguageName switch
    {
        "en" => s_en,
        _ => s_en,
    };
}

