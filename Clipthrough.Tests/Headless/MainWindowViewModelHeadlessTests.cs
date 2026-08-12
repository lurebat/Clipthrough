using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Clipthrough.Database;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using System.Reactive.Threading.Tasks;
using Xunit;

namespace Clipthrough.Tests.Headless;

public sealed class MainWindowViewModelHeadlessTests
{
    [AvaloniaFact]
    public async Task CapturedClipRefresh_SelectsNewestClip()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var firstClip = await CaptureTextClipAsync(scope.ClipStoreService, "first");
        clipboardMonitor.Emit(firstClip);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        var secondClip = await CaptureTextClipAsync(scope.ClipStoreService, "second");
        clipboardMonitor.Emit(secondClip);
        for (var attempt = 0; attempt < 10 && viewModel.SelectedClip?.Id != secondClip.Id; attempt++)
        {
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(secondClip.Id, viewModel.SelectedClip?.Id);
    }

    [AvaloniaFact]
    public async Task CapturedClipRefresh_BurstsCoalesceIntoLatestRefresh()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        var clipStore = new SlowSearchClipStore();
        using var viewModel = new MainWindowViewModel(
            clipStore,
            clipboardMonitor,
            new TestClipSampleDataService(),
            scope.SettingsService,
            systemInteraction,
            scope.StorageOptionsService,
            scope.SensitivityService,
            scope.NotificationService,
            sessionLogService,
            scope.ClipExportService,
            new TestImageEditorService(),
            scope.SearchHistoryService,
            new TestAiTransformService(),
            new TestOcrService(),
            new NoOpBackgroundOcrQueue(),
            new Clipthrough.Services.BackgroundJobIndicator(),
            scope.DatabaseInitializer);

        await viewModel.InitializeAsync();

        for (var i = 1; i <= 6; i++)
        {
            var clip = new ClipEntry
            {
                Id = i,
                Content = $"clip {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"clip {i}"),
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                SourceApp = "Tests",
                Hash = $"hash-{i}",
            };
            clipStore.Upsert(clip);
            clipboardMonitor.Emit(clip);
        }

        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(6, viewModel.SelectedClip?.Id);
        Assert.InRange(clipStore.SearchCallCount, 2, 4);
        Assert.Equal(1, clipStore.MaxConcurrentSearches);
    }

    [AvaloniaFact]
    public async Task DefaultAutoSelection_SkipsPinnedClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);
        await viewModel.InitializeAsync();

        var pinned = new ClipEntry
        {
            Id = 1,
            Content = "pinned",
            ContentBytes = Encoding.UTF8.GetBytes("pinned"),
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = "Tests",
            Hash = "pinned",
            PinnedAt = DateTimeOffset.UtcNow,
            LastCopiedAt = DateTimeOffset.UtcNow,
            FirstCopiedAt = DateTimeOffset.UtcNow,
        };
        var unpinned = new ClipEntry
        {
            Id = 2,
            Content = "unpinned",
            ContentBytes = Encoding.UTF8.GetBytes("unpinned"),
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = "Tests",
            Hash = "unpinned",
            LastCopiedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            FirstCopiedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        };

        viewModel.Clips.Add(new ClipItemViewModel(pinned));
        viewModel.Clips.Add(new ClipItemViewModel(unpinned));

        Assert.Equal(unpinned.Id, viewModel.GetDefaultAutoSelectedClip()?.Id);
    }

    [AvaloniaFact]
    public async Task ImageAiTransforms_AreHiddenWhenAiIsNotConfigured()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var clip = await CaptureImageClipAsync(scope.ClipStoreService, CreatePngBytes(0), "image");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.IsAiMenuVisible);
        Assert.False(viewModel.HasImageTransformTarget);
        Assert.False(viewModel.HasTransformableTarget);
        Assert.Empty(viewModel.VisibleAiMenuEntries);
    }

    [AvaloniaFact]
    public async Task FileClip_CanUseTextTransformations()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var clip = await CaptureFilesClipAsync(scope.ClipStoreService, ["C:\\Temp\\alpha.txt", "D:\\Data\\beta.txt"]);
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.SelectedClip?.CanTransform);
        Assert.True(viewModel.HasTextTransformTarget);

        await viewModel.ApplyTextTransformationCommand.Execute(TextTransformation.UpperCase).ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal($"C:\\TEMP\\ALPHA.TXT{Environment.NewLine}D:\\DATA\\BETA.TXT", systemInteraction.LastCopiedText);
    }

    [AvaloniaFact]
    public async Task SelectAllAndFavoriteSelected_UpdateAllCheckedClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var firstClip = await CaptureTextClipAsync(scope.ClipStoreService, "first");
        clipboardMonitor.Emit(firstClip);
        var secondClip = await CaptureTextClipAsync(scope.ClipStoreService, "second");
        clipboardMonitor.Emit(secondClip);
        Dispatcher.UIThread.RunJobs();

        await viewModel.SelectAllClipsCommand.Execute().ToTask();

        Assert.Equal(2, viewModel.CheckedClipCount);

        await viewModel.FavoriteCheckedClipsCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.All(viewModel.Clips, clip => Assert.True(clip.IsFavorite));
    }

    [AvaloniaFact]
    public async Task FavoriteCheckedClips_SurvivesRefreshSnapshotTakenBeforeTheWrite()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        var gatedStore = new GatedSearchClipStore(scope.ClipStoreService);
        using var viewModel = new MainWindowViewModel(
            gatedStore,
            clipboardMonitor,
            new TestClipSampleDataService(),
            scope.SettingsService,
            systemInteraction,
            scope.StorageOptionsService,
            scope.SensitivityService,
            scope.NotificationService,
            sessionLogService,
            scope.ClipExportService,
            new TestImageEditorService(),
            scope.SearchHistoryService,
            new TestAiTransformService(),
            new TestOcrService(),
            new NoOpBackgroundOcrQueue(),
            new Clipthrough.Services.BackgroundJobIndicator(),
            scope.DatabaseInitializer);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var firstClip = await CaptureTextClipAsync(scope.ClipStoreService, "first");
        clipboardMonitor.Emit(firstClip);
        var secondClip = await CaptureTextClipAsync(scope.ClipStoreService, "second");
        clipboardMonitor.Emit(secondClip);

        for (var attempt = 0; attempt < 20 && viewModel.Clips.Count < 2; attempt++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        // Let the throttled capture refreshes drain so the gate below parks the
        // refresh this test actually cares about.
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, viewModel.Clips.Count);

        await viewModel.SelectAllClipsCommand.Execute().ToTask();
        Assert.Equal(2, viewModel.CheckedClipCount);

        // Park a refresh right after it has read the pre-write database state.
        gatedStore.ArmGate();
        var refresh = viewModel.RefreshCommand.Execute().ToTask();
        for (var attempt = 0; attempt < 100 && !gatedStore.IsParked; attempt++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(gatedStore.IsParked, "the gated search never reached its park point");

        // Favourite both clips while that now-stale snapshot is still in flight.
        await viewModel.FavoriteCheckedClipsCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        gatedStore.ReleaseGate();
        await refresh;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, viewModel.Clips.Count);
        Assert.All(
            viewModel.Clips,
            clip => Assert.True(clip.IsFavorite, $"clip {clip.Id} was rolled back to not-favorite by a stale refresh"));
    }

    [AvaloniaFact]
    public async Task CopyEditedClip_CopiesModifiedText()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "original");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        viewModel.EditedClipText = "edited";

        Assert.True(viewModel.ShowCopyEditedClipButton);

        await viewModel.CopyEditedClipCommand.Execute().ToTask();

        Assert.Equal("edited", systemInteraction.LastCopiedText);
        Assert.Equal(AppText.EditedClipCopiedStatus, viewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task CommitEditedClipOnFocusLoss_DoesNotCopyAutomatically()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "original");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        viewModel.EditedClipText = "edited";
        await viewModel.CommitEditedClipOnFocusLossAsync();

        Assert.Null(systemInteraction.LastCopiedText);
        Assert.True(viewModel.ShowCopyEditedClipButton);
    }

    [AvaloniaFact]
    public async Task RichContent_RenderedMode_IsEditableForHtml()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        const string originalMarkup = "<p><span style=\"color:#ff0000\">original</span></p>";
        var clip = await CaptureRichTextClipAsync(scope.ClipStoreService, originalMarkup, ClipContentFormat.Html);
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.ShowSelectedRichTextRenderer);
        Assert.False(viewModel.IsSelectedClipTextEditable);
        Assert.True(viewModel.CanEditSelectedRichTextInRenderedMode);
        Assert.True(viewModel.ShowCopyEditedClipButton);
    }

    [AvaloniaFact]
    public async Task RichContent_IsViewOnlyForRtf()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        const string rtf = @"{\rtf1\ansi{\colortbl ;\red255\green0\blue0;}\cf1 original}";
        var clip = await CaptureRichTextClipAsync(scope.ClipStoreService, rtf, ClipContentFormat.Rtf);
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.ShowSelectedRichTextRenderer);
        Assert.False(viewModel.IsSelectedClipTextEditable);
        Assert.False(viewModel.ShowCopyEditedClipButton);
    }

    [AvaloniaFact]
    public async Task HtmlRichContent_RenderedMode_EnablesCopyAsNewEditing()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        const string originalMarkup = "<p><strong>original</strong></p>";
        var clip = await CaptureRichTextClipAsync(scope.ClipStoreService, originalMarkup, ClipContentFormat.Html);
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        viewModel.SelectedContentDisplayMode = ContentDisplayMode.Rendered;
        viewModel.EditedClipText = "<p><em>edited</em></p>";

        Assert.True(viewModel.ShowSelectedRichTextRenderer);
        Assert.True(viewModel.CanEditSelectedRichTextInRenderedMode);
        Assert.True(viewModel.ShowCopyEditedClipButton);

        await viewModel.CopyEditedClipCommand.Execute().ToTask();

        Assert.Equal("<p><em>edited</em></p>", systemInteraction.LastCopiedRichContent);
        Assert.Equal(ClipContentFormat.Html, systemInteraction.LastCopiedRichContentFormat);
        Assert.Equal(AppText.EditedClipCopiedStatus, viewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task RtfRichContent_RenderedMode_RemainsReadOnly()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        const string rtf = @"{\rtf1\ansi{\colortbl ;\red255\green0\blue0;}\cf1 original}";
        var clip = await CaptureRichTextClipAsync(scope.ClipStoreService, rtf, ClipContentFormat.Rtf);
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        viewModel.SelectedContentDisplayMode = ContentDisplayMode.Rendered;

        Assert.True(viewModel.ShowSelectedRichTextRenderer);
        Assert.False(viewModel.CanEditSelectedRichTextInRenderedMode);
        Assert.False(viewModel.ShowCopyEditedClipButton);
    }

    [AvaloniaFact]
    public async Task CopyEditedImage_CapturesEditedImageAsNewClip()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        var clip = CreateImageClipEntry(CreatePngBytes(unchecked((int)0xFFFF0000)), "original image");
        viewModel.SelectedClip = new ClipItemViewModel(clip);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ContentType.Image, viewModel.SelectedClip!.Clip.ContentType);
        Assert.NotNull(viewModel.SelectedClip.Clip.ContentBytes);
        Assert.NotEmpty(viewModel.SelectedClip.Clip.ContentBytes!);
        Assert.True(viewModel.ShowSelectedImageEditor);

        await viewModel.CopyEditedImageAsync(CreatePngBytes(unchecked((int)0xFF00FF00)));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, systemInteraction.BitmapCopyCount);
        Assert.Equal(AppText.EditedImageCopiedStatus, viewModel.StatusText);
        Assert.NotNull(viewModel.SelectedClip);
        Assert.NotEqual(clip.Id, viewModel.SelectedClip!.Id);
        Assert.Equal(ContentType.Image, viewModel.SelectedClip.Clip.ContentType);
    }

    [AvaloniaFact]
    public async Task SessionLogs_FilterByLevelAndSearch()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        sessionLogService.Emit(new SessionLogEntry
        {
            Level = AppNotificationLevel.Information,
            Message = "Clipboard monitor attached."
        });
        sessionLogService.Emit(new SessionLogEntry
        {
            Level = AppNotificationLevel.Warning,
            Message = "Payload exceeded limit."
        });
        Dispatcher.UIThread.RunJobs();

        await viewModel.SessionLogs.OpenCommand.Execute().ToTask();
        viewModel.SessionLogs.SelectedLogLevelOption = viewModel.SessionLogs.LogLevelOptions[2];
        viewModel.SessionLogs.SearchText = "limit";
        Dispatcher.UIThread.RunJobs();

        Assert.Single(viewModel.SessionLogs.VisibleSessionLogs);
        Assert.Equal("Payload exceeded limit.", viewModel.SessionLogs.VisibleSessionLogs[0].Message);
    }

    [AvaloniaFact]
    public async Task InitializeAsync_WhenSetupIsMissing_OpensWelcome()
    {
        using var scope = new TemporaryDatabaseScope();
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsWelcomeOpen);
        Assert.Equal(AppText.WelcomeStatusText, viewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task InitializeAsync_WhenOnlySettingsAreMissing_DoesNotOpenWelcome()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.StorageOptionsService.SetHasSavedConfig(true);
        await scope.DatabaseInitializer.InitializeAsync();

        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        Assert.False(viewModel.IsWelcomeOpen);

        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "existing");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(clip.Id, viewModel.SelectedClip?.Id);
    }

    [AvaloniaFact]
    public async Task InitializeAsync_WhenDatabaseIsMissing_OpensWelcome()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.StorageOptionsService.SetHasSavedConfig(true);
        scope.SettingsService.SetHasSavedSettings(true);

        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsWelcomeOpen);
        Assert.Equal(AppText.WelcomeStatusText, viewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task InitializeAsync_RestoresPersistedFiltersAfterSettingsLoad()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.StorageOptionsService.SetHasSavedConfig(true);
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrentOnInitialize(AppSettings.Default with
        {
            LastShowFavoritesOnly = true,
            LastShowSensitiveOnly = true,
            LastShowPastedOnly = true,
            LastUseRegexSearch = true,
            LastCaseSensitiveSearch = true,
            LastUseWildcardSearch = true,
            LastWholeWordSearch = true,
            LastUseFuzzyClipSearch = true,
            LastUseSemanticClipSearch = false,
            LastContentTypeFilter = ContentType.Image,
        });

        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.ShowFavoritesOnly);
        Assert.True(viewModel.ShowSensitiveOnly);
        Assert.True(viewModel.ShowPastedOnly);
        Assert.True(viewModel.UseRegexSearch);
        Assert.True(viewModel.CaseSensitiveSearch);
        Assert.True(viewModel.UseWildcardSearch);
        Assert.True(viewModel.WholeWordSearch);
        Assert.True(viewModel.UseFuzzyClipSearch);
        Assert.False(viewModel.UseSemanticClipSearch);
        Assert.Equal(ContentType.Image, viewModel.SelectedContentTypeOption.Value);
    }

    // Characterization guard for the SettingsViewModel extraction (#10): opening
    // settings must load every section's draft from the current AppSettings.
    // As sections move to MainWindowViewModel.Settings, repoint the property
    // access here (SettingsX -> Settings.X); the asserted values stay constant.
    [AvaloniaFact]
    public async Task OpenSettings_LoadsDraftFromCurrentSettings()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.StorageOptionsService.SetHasSavedConfig(true);
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrentOnInitialize(AppSettings.Default with
        {
            EnableAi = true,
            AiProvider = Models.AiProvider.Copilot,
            AiBaseUrl = "https://ai.example/v1",
            AiApiKey = "sk-char-test",
            AiModel = "gpt-char",
            AiImageModel = "img-char",
            AiReasoningEffort = "high",
            EnableAutoUpdate = false,
            AutoApplyUpdatesOnStartup = true,
            UpdateFeedUrl = "https://feed.example/x",
            OcrLanguages = "en-US,fr-FR",
            AutoOcrImageClips = true,
            ThemeMode = Models.ThemeMode.Light,
            CloseToTray = true,
            // Hotkeys section
            EnableToggleRegexHotkey = true,
            ToggleRegexHotkey = "Ctrl+R",
            EnableToggleFavoritesHotkey = true,
            ToggleFavoritesHotkey = "Ctrl+F",
            EnableToggleWindowHotkey = true,
            ToggleWindowHotkey = "Ctrl+Shift+V",
            EnableIncrementalPasteHotkey = true,
            IncrementalPasteHotkey = "Ctrl+OemOpenBrackets",
            EnableCopyAndFavoriteHotkey = false,
            CopyAndFavoriteHotkey = "Ctrl+Alt+F",
        });

        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);
        await viewModel.InitializeAsync();

        viewModel.OpenSettingsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        // AI section
        Assert.True(viewModel.Settings.EnableAi);
        Assert.Equal(Models.AiProvider.Copilot, viewModel.Settings.AiProvider);
        Assert.Equal("https://ai.example/v1", viewModel.Settings.AiBaseUrl);
        Assert.Equal("sk-char-test", viewModel.Settings.AiApiKey);
        Assert.Equal("gpt-char", viewModel.Settings.AiModel);
        Assert.Equal("img-char", viewModel.Settings.AiImageModel);
        Assert.Equal("high", viewModel.Settings.AiReasoningEffort);
        // Update section
        Assert.False(viewModel.Settings.EnableAutoUpdate);
        Assert.True(viewModel.Settings.AutoApplyUpdatesOnStartup);
        Assert.Equal("https://feed.example/x", viewModel.Settings.UpdateFeedUrl);
        // OCR section
        Assert.Equal("en-US,fr-FR", viewModel.Settings.OcrLanguages);
        Assert.True(viewModel.Settings.AutoOcrImageClips);
        // Theme / misc
        Assert.Equal(Models.ThemeMode.Light, viewModel.Settings.ThemeMode);
        Assert.True(viewModel.Settings.CloseToTray);
        // Hotkeys section
        Assert.True(viewModel.Settings.EnableToggleRegexHotkey);
        Assert.Equal("Ctrl+R", viewModel.Settings.ToggleRegexHotkey);
        Assert.True(viewModel.Settings.EnableToggleFavoritesHotkey);
        Assert.Equal("Ctrl+F", viewModel.Settings.ToggleFavoritesHotkey);
        Assert.True(viewModel.Settings.EnableToggleWindowHotkey);
        Assert.Equal("Ctrl+Shift+V", viewModel.Settings.ToggleWindowHotkey);
        Assert.True(viewModel.Settings.EnableIncrementalPasteHotkey);
        Assert.Equal("Ctrl+OemOpenBrackets", viewModel.Settings.IncrementalPasteHotkey);
        Assert.False(viewModel.Settings.EnableCopyAndFavoriteHotkey);
        Assert.Equal("Ctrl+Alt+F", viewModel.Settings.CopyAndFavoriteHotkey);
    }

    // Regression guard for the U12 read-model split: list/search reads omit image
    // bytes (ClipEntry.ContentBytes == null) to keep the list query light. When a
    // clip is shown or selected, ClipItemViewModel must lazily reload the full entry
    // by id so preview/edit/export/drag/AI-image have the bytes again.
    [AvaloniaFact]
    public async Task EnsureContentHydratedAsync_LoadsBytesForMetadataOnlyImageClip()
    {
        var metaOnly = new Clipthrough.Models.ClipEntry
        {
            Id = 42,
            ContentType = Clipthrough.Models.ContentType.Image,
            ByteSize = 3,
            Hash = "h",
        };
        var bytes = new byte[] { 1, 2, 3 };
        var full = new Clipthrough.Models.ClipEntry
        {
            Id = 42,
            ContentType = Clipthrough.Models.ContentType.Image,
            ContentBytes = bytes,
            ByteSize = 3,
            Hash = "h",
        };
        var hydrateCalls = 0;
        var item = new Clipthrough.ViewModels.ClipItemViewModel(metaOnly, contentHydrator: id =>
        {
            hydrateCalls++;
            return Task.FromResult<Clipthrough.Models.ClipEntry?>(id == 42 ? full : null);
        });

        Assert.Null(item.Clip.ContentBytes);

        await item.EnsureContentHydratedAsync();

        Assert.Same(bytes, item.Clip.ContentBytes);
        Assert.Equal(1, hydrateCalls);

        // Idempotent once hydrated: no extra store round-trip.
        await item.EnsureContentHydratedAsync();
        Assert.Equal(1, hydrateCalls);
    }

    // The per-row badge WrapPanel was collapsed into a single precomputed MetaLine
    // (type · age · markers) for cheaper row realization. Guard that it composes the
    // state markers and is rebuilt when favorite/pin toggle at runtime.
    [AvaloniaFact]
    public void MetaLine_ComposesStateMarkers_AndRebuildsOnToggle()
    {
        var clip = new Clipthrough.Models.ClipEntry
        {
            Id = 7,
            ContentType = Clipthrough.Models.ContentType.Text,
            Content = "hello",
            Hash = "h",
            CopyCount = 3,
            PasteCount = 2,
            IsFavorite = true,
            PinnedAt = DateTimeOffset.UtcNow,
            LastCopiedAt = DateTimeOffset.UtcNow,
        };
        var item = new Clipthrough.ViewModels.ClipItemViewModel(clip);

        Assert.Contains(item.DisplayContentType, item.MetaLine);
        Assert.Contains("★", item.MetaLine);      // favorite marker
        Assert.Contains("📌", item.MetaLine);      // pinned marker
        Assert.Contains("Pasted", item.MetaLine);  // pasted marker (PasteCount > 0)

        item.SetFavoriteState(false);
        Assert.DoesNotContain("★", item.MetaLine);

        item.SetPinnedState(false);
        Assert.DoesNotContain("📌", item.MetaLine);
    }

    // The row meta renders as colored inline Runs (controls:MetaInlines) so the line
    // wraps and keeps per-token colour instead of one muted, truncated string.
    [AvaloniaFact]
    public void MetaSegments_RenderAsColoredInlineRuns()
    {
        var clip = new Clipthrough.Models.ClipEntry
        {
            Id = 8,
            ContentType = Clipthrough.Models.ContentType.Text,
            Content = "hello",
            Hash = "h",
            CopyCount = 3,
            LastCopiedAt = DateTimeOffset.UtcNow,
        };
        var item = new Clipthrough.ViewModels.ClipItemViewModel(clip);

        // type + age + copy-count => 3 colored tokens.
        Assert.Equal(3, item.MetaSegments.Count);
        Assert.Equal(item.DisplayContentType, item.MetaSegments[0].Text);

        var textBlock = new Avalonia.Controls.TextBlock();
        Clipthrough.Controls.MetaInlines.SetSegments(textBlock, item.MetaSegments);

        // 3 segment runs + 2 separator runs = 5 inlines.
        Assert.NotNull(textBlock.Inlines);
        Assert.Equal(5, textBlock.Inlines!.Count);
    }

    [AvaloniaFact]
    public void Dispose_PersistsCurrentFilterStateImmediately()
    {
        using var scope = new TemporaryDatabaseScope();
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        viewModel.ShowFavoritesOnly = true;
        viewModel.ShowSensitiveOnly = true;
        viewModel.ShowPastedOnly = true;
        viewModel.UseRegexSearch = true;
        viewModel.CaseSensitiveSearch = true;
        viewModel.UseWildcardSearch = true;
        viewModel.WholeWordSearch = true;
        viewModel.UseFuzzyClipSearch = true;
        viewModel.UseSemanticClipSearch = false;
        viewModel.IsImageTypeSelected = true;

        viewModel.Dispose();

        Assert.True(scope.SettingsService.Current.LastShowFavoritesOnly);
        Assert.True(scope.SettingsService.Current.LastShowSensitiveOnly);
        Assert.True(scope.SettingsService.Current.LastShowPastedOnly);
        Assert.True(scope.SettingsService.Current.LastUseRegexSearch);
        Assert.True(scope.SettingsService.Current.LastCaseSensitiveSearch);
        Assert.True(scope.SettingsService.Current.LastUseWildcardSearch);
        Assert.True(scope.SettingsService.Current.LastWholeWordSearch);
        Assert.True(scope.SettingsService.Current.LastUseFuzzyClipSearch);
        Assert.False(scope.SettingsService.Current.LastUseSemanticClipSearch);
        // The single-value LastContentTypeFilter is legacy and is deliberately
        // nulled on save; the multi-select list is the live contract.
        Assert.Null(scope.SettingsService.Current.LastContentTypeFilter);
        Assert.Equal([ContentType.Image], scope.SettingsService.Current.LastContentTypeFilters);
        Assert.True(scope.SettingsService.SaveCallCount > 0);
    }

    [AvaloniaFact]
    public async Task SaveSettings_FromWelcome_ClosesWelcomeAndStartsApp()
    {
        using var scope = new TemporaryDatabaseScope();
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        await viewModel.SaveSettingsCommand.Execute().ToTask();

        // Saving from the welcome screen kicks off StartDatabaseInBackgroundAsync,
        // which opens the database on a worker thread and only then posts the
        // welcome dismissal back to the UI thread. Pump until it lands.
        for (var attempt = 0; attempt < 50 && viewModel.IsWelcomeOpen; attempt++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.False(viewModel.IsWelcomeOpen);
        Assert.True(scope.SettingsService.HasSavedSettings);
        Assert.True(scope.StorageOptionsService.HasSavedConfig);
        Assert.True(scope.StorageOptionsService.DatabaseExists);
    }

    [AvaloniaFact]
    public async Task ExportSelected_CopiesAndOpensExportPath()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "export me");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        await viewModel.ExportSelectedCommand.Execute().ToTask();

        Assert.Equal(scope.ClipExportService.LastPrimaryPath, systemInteraction.LastCopiedText);
        Assert.Equal(scope.ClipExportService.LastPrimaryPath, systemInteraction.LastOpenedPath);
    }

    [AvaloniaFact]
    public async Task ToggleFavoriteCommand_TogglesSelectedClipOnAndOff()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "favorite me");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        await viewModel.ToggleFavoriteCommand.Execute().ToTask();

        Assert.True(viewModel.SelectedClip?.IsFavorite);
        Assert.Equal(AppText.FavoriteButtonLabel, viewModel.SelectedClipFavoriteButtonLabel);

        await viewModel.ToggleFavoriteCommand.Execute().ToTask();

        Assert.False(viewModel.SelectedClip?.IsFavorite);
        Assert.Equal(AppText.FavoriteButtonLabel, viewModel.SelectedClipFavoriteButtonLabel);
    }

    [AvaloniaFact]
    public async Task ClipRowToggleFavoriteCommand_TogglesOnAndOff()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "row favorite");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        var clipViewModel = Assert.Single(viewModel.Clips);

        await clipViewModel.ToggleFavoriteCommand.Execute().ToTask();
        Assert.True(clipViewModel.IsFavorite);

        await clipViewModel.ToggleFavoriteCommand.Execute().ToTask();
        Assert.False(clipViewModel.IsFavorite);
    }

    [AvaloniaFact]
    public async Task FavoriteCheckedClips_UsesCurrentSelectionWhenNothingIsChecked()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "favorite current");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        await viewModel.FavoriteCheckedClipsCommand.Execute().ToTask();

        Assert.True(viewModel.SelectedClip?.IsFavorite);

        await viewModel.FavoriteCheckedClipsCommand.Execute().ToTask();

        Assert.False(viewModel.SelectedClip?.IsFavorite);
    }

    [AvaloniaFact]
    public async Task CopySelected_SensitiveClip_PublishesActionableNotification()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "password=supersecret");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        await viewModel.CopySelectedCommand.Execute().ToTask();

        Assert.NotNull(scope.NotificationService.LastNotification);
        Assert.True(scope.NotificationService.LastNotification!.IsPersistent);
        Assert.Equal(3, scope.NotificationService.LastNotification.Actions.Count);
    }

    /// <summary>
    /// Regression test for S1: a drag-and-drop import used to call
    /// CaptureFastAsync and stop there. CaptureFastAsync deliberately skips the
    /// sensitivity scan for speed — the clipboard monitor finishes the job in
    /// EnrichCapturedClipAsync, but a drop has no monitor behind it. A dropped
    /// credential therefore stayed classified as ordinary content: rendered in
    /// plaintext, exempt from the sensitive-clip lifetime, and returned by
    /// ordinary searches.
    /// </summary>
    [AvaloniaFact]
    public async Task ImportDroppedData_ClassifiesSensitiveContent()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();

        // Payload matches the sensitivity rule seeded below.
        var dragDrop = new StubDragDropService(
        [
            new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = "SECRET-4711",
                ContentBytes = System.Text.Encoding.UTF8.GetBytes("SECRET-4711"),
            },
        ]);

        using var viewModel = CreateViewModel(
            scope, clipboardMonitor, systemInteraction, sessionLogService, dragDropService: dragDrop);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        // Seed a rule that certainly matches the dropped payload, after VM
        // initialization so nothing overwrites it.
        await scope.SensitivityService.SaveRulesAsync(
        [
            new SensitivityRule
            {
                Name = "test-secret",
                Pattern = "SECRET-[0-9]+",
                Severity = "critical",
                IsEnabled = true,
                IsBuiltIn = false,
            },
        ]);
        await scope.SensitivityService.ReloadAsync();

        var imported = await viewModel.ImportDroppedDataAsync(new Avalonia.Input.DataTransfer(), null);
        Assert.Equal(1, imported);

        var stored = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());
        var clip = Assert.Single(stored.Items);
        Assert.True(clip.IsSensitive, "A dropped credential must be classified exactly like a copied one.");
        Assert.NotEmpty(clip.SensitivityMatches);
    }

    /// <summary>
    /// Regression test for S1: dropped images used to skip the OCR enqueue the
    /// clipboard-capture stream performs, so their text was unsearchable until
    /// an unrelated backlog run happened to pick them up.
    /// </summary>
    [AvaloniaFact]
    public async Task ImportDroppedData_EnqueuesImageClipsForOcr()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        var ocrQueue = new NoOpBackgroundOcrQueue();

        scope.SettingsService.SetCurrent(scope.SettingsService.Current with { AutoOcrImageClips = true });

        var pngBytes = CreatePngBytes(0);
        var dragDrop = new StubDragDropService(
        [
            new ClipCaptureRequest
            {
                ContentType = ContentType.Image,
                ContentFormat = ClipContentFormat.Bitmap,
                ContentText = "image",
                ContentBytes = pngBytes,
            },
        ]);

        using var viewModel = CreateViewModel(
            scope, clipboardMonitor, systemInteraction, sessionLogService,
            backgroundOcrQueue: ocrQueue, dragDropService: dragDrop,
            ocrService: new TestOcrService(isAvailable: true));

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var imported = await viewModel.ImportDroppedDataAsync(new Avalonia.Input.DataTransfer(), null);
        Assert.Equal(1, imported);

        Assert.Single(ocrQueue.Enqueued);
    }

    private static MainWindowViewModel CreateViewModel(
        TemporaryDatabaseScope scope,
        TestClipboardMonitorService clipboardMonitor,
        TestSystemInteractionService systemInteraction,
        TestSessionLogService sessionLogService,
        TestImageEditorService? imageEditorService = null,
        Clipthrough.Services.IBackgroundOcrQueue? backgroundOcrQueue = null,
        Clipthrough.Services.IDragDropService? dragDropService = null,
        Clipthrough.Services.IOcrService? ocrService = null,
        IClipStoreService? clipStore = null)
    {
        return new MainWindowViewModel(
            clipStore ?? scope.ClipStoreService,
            clipboardMonitor,
            new TestClipSampleDataService(),
            scope.SettingsService,
            systemInteraction,
            scope.StorageOptionsService,
            scope.SensitivityService,
            scope.NotificationService,
            sessionLogService,
            scope.ClipExportService,
            imageEditorService ?? new TestImageEditorService(),
            scope.SearchHistoryService,
            new TestAiTransformService(),
            ocrService ?? new TestOcrService(),
            backgroundOcrQueue ?? new NoOpBackgroundOcrQueue(),
            new Clipthrough.Services.BackgroundJobIndicator(),
            scope.DatabaseInitializer,
            dragDropService: dragDropService);
    }

    private sealed class NoOpBackgroundOcrQueue : Clipthrough.Services.IBackgroundOcrQueue
    {
        private readonly List<long> _enqueued = [];

        public IReadOnlyList<long> Enqueued => _enqueued;

        public IObservable<long> OcrCompleted { get; } = System.Reactive.Linq.Observable.Empty<long>();
        public IObservable<System.Reactive.Unit> QueueChanged { get; } = System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();
        public void Start() { }
        public Task StopAsync() => Task.CompletedTask;
        public void Enqueue(long clipId) => _enqueued.Add(clipId);
        public Task EnqueueBacklogAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Returns a fixed set of capture requests for any drop, so drag-import tests
    /// don't have to build a real platform data transfer.
    /// </summary>
    private sealed class StubDragDropService(IReadOnlyList<ClipCaptureRequest> requests) : Clipthrough.Services.IDragDropService
    {
        public Task<Avalonia.Input.IDataTransfer> BuildDragPayloadAsync(
            IReadOnlyList<ClipEntry> clips,
            Avalonia.Platform.Storage.IStorageProvider storageProvider) => throw new NotSupportedException();

        public Task<IReadOnlyList<ClipCaptureRequest>> TryBuildCaptureRequestsAsync(
            Avalonia.Input.IDataTransfer drop,
            ClipboardSourceApplicationInfo? sourceInfo) => Task.FromResult(requests);
    }

    private sealed class SlowSearchClipStore : IClipStoreService
    {
        private readonly object _sync = new();
        private readonly List<ClipEntry> _items = [];
        private int _activeSearches;
        private int _searchCallCount;

        public int SearchCallCount => _searchCallCount;

        public int MaxConcurrentSearches { get; private set; }

        public void Upsert(ClipEntry clip)
        {
            lock (_sync)
            {
                _items.RemoveAll(item => item.Id == clip.Id);
                _items.Add(clip);
            }
        }

        public async Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeSearches);
            MaxConcurrentSearches = Math.Max(MaxConcurrentSearches, active);
            Interlocked.Increment(ref _searchCallCount);

            try
            {
                await Task.Delay(120, cancellationToken);
                List<ClipEntry> snapshot;
                lock (_sync)
                {
                    snapshot = _items
                        .OrderByDescending(item => item.Id)
                        .Skip(filters.Offset)
                        .Take(filters.Limit)
                        .ToList();
                }

                return new ClipSearchResult
                {
                    Items = snapshot,
                    TotalMatchingCount = snapshot.Count,
                    TotalClipCount = _items.Count,
                    SensitiveClipCount = 0,
                    TotalStoredBytes = snapshot.Sum(static item => item.ByteSize),
                    LastCapturedAt = null,
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeSearches);
            }
        }

        public Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ApplyPendingSensitivityAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OcrCoverage(0, 0, 0, 0, 0));
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ClipMaintenanceResult());
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult<IReadOnlyList<ClipEntry>>(_items.Where(item => clipIds.Contains(item.Id)).ToList());
            }
        }
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => Task.FromResult(new EmbeddingCoverage(0, 0, 0, 0, 0));
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Wraps a real store and can park a single <see cref="SearchAsync"/> call
    /// after it has read the database but before the caller applies the result.
    /// That is the exact window in which a clip write turns a refresh snapshot
    /// stale.
    /// </summary>
    private sealed class GatedSearchClipStore(IClipStoreService inner) : IClipStoreService
    {
        private TaskCompletionSource? _armed;
        private TaskCompletionSource? _parked;
        private volatile bool _isParked;

        public bool IsParked => _isParked;

        public void ArmGate() => Volatile.Write(ref _armed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseGate() => Volatile.Read(ref _parked)?.TrySetResult();

        public async Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default)
        {
            var result = await inner.SearchAsync(filters, cancellationToken);

            var gate = Interlocked.Exchange(ref _armed, null);
            if (gate is not null)
            {
                Volatile.Write(ref _parked, gate);
                _isParked = true;
                try
                {
                    // Never hang the suite if the test forgets to release.
                    await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }
                finally
                {
                    _isParked = false;
                    Volatile.Write(ref _parked, null);
                }
            }

            return result;
        }

        public Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default) => inner.CaptureBatchAsync(requests, cancellationToken);
        public Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => inner.CaptureAsync(request, cancellationToken);
        public Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => inner.CaptureFastAsync(request, cancellationToken);
        public Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default) => inner.UpdateDeferredContentAsync(clipId, request, cancellationToken);
        public Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default) => inner.UpdateSourceAppIconAsync(clipId, iconBytes, cancellationToken);
        public Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default) => inner.ApplySensitivityAsync(clipId, cancellationToken);
        public Task<int> ApplyPendingSensitivityAsync(CancellationToken cancellationToken = default) => inner.ApplyPendingSensitivityAsync(cancellationToken);
        public Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default) => inner.SetFavoriteAsync(clipId, isFavorite, cancellationToken);
        public Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default) => inner.SetPinnedAsync(clipId, isPinned, cancellationToken);
        public Task DeleteAsync(long clipId, CancellationToken cancellationToken = default) => inner.DeleteAsync(clipId, cancellationToken);
        public Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default) => inner.ClearSensitivityAsync(clipId, cancellationToken);
        public Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default) => inner.SetSensitiveAsync(clipId, isSensitive, cancellationToken);
        public Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkPastedAsync(clipId, cancellationToken);
        public Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default) => inner.TryClaimForOcrAsync(clipId, cancellationToken);
        public Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default) => inner.SetOcrResultAsync(clipId, ocrText, cancellationToken);
        public Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetOcrFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default) => inner.GetPendingOcrClipIdsAsync(cancellationToken);
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkOcrForRerunAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllSucceededForRerunAsync(cancellationToken);
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => inner.GetOcrCoverageAsync(cancellationToken);
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => inner.ApplyMaintenanceAsync(cancellationToken);
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => inner.RebuildSensitivityMatchesAsync(cancellationToken);
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => inner.GetClipAtOffsetAsync(offset, cancellationToken);
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetByIdAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.GetByIdsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => inner.ClaimPendingEmbeddingsAsync(batchSize, cancellationToken);
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => inner.SaveEmbeddingBatchAsync(records, modelVersion, cancellationToken);
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetEmbeddingFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllEmbeddingsForRerunAsync(cancellationToken);
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => inner.GetEmbeddingCoverageAsync(cancellationToken);
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => inner.LoadAllEmbeddingsAsync(cancellationToken);
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => inner.PrewarmAsync(cancellationToken);
    }

    /// <summary>
    /// The refresh loop retries when a clip write is still in flight, and it can
    /// also discard a result and requeue. Recording the query at request-build
    /// time meant the retry compared the new query against itself, decided
    /// "unchanged", and re-read at the old depth - so changing the search never
    /// reset paging whenever a write happened to straddle it.
    /// </summary>
    [AvaloniaFact]
    public async Task Refresh_ResetsPaging_EvenWhenAClipWriteStraddlesTheQueryChange()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var seedRequests = Enumerable.Range(1, 201)
            .Select(i => new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"keep {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"keep {i}"),
            })
            .ToList();
        Assert.Equal(201, (await scope.ClipStoreService.CaptureBatchAsync(seedRequests)).Imported);

        var blockingStore = new BlockingWriteClipStore(scope.ClipStoreService);
        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            clipStore: blockingStore);
        await viewModel.InitializeAsync();

        for (var attempt = 0; attempt < 80 && viewModel.Clips.Count < 200; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(200, viewModel.Clips.Count);
        await viewModel.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(201, viewModel.Clips.Count);

        // Hold a write open so every refresh attempt sees a pending mutation and
        // takes the retry path.
        blockingStore.BlockWrites = true;
        viewModel.SelectedClip = viewModel.Clips[0];
        var pendingWrite = viewModel.ToggleFavoriteCommand.Execute().ToTask();

        for (var attempt = 0; attempt < 80 && !blockingStore.IsWriteBlocked; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(blockingStore.IsWriteBlocked);

        // Change the query while that write is still open.
        viewModel.SearchText = "keep";
        for (var attempt = 0; attempt < 200 && blockingStore.CountSearchesFor("keep") < 2; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(blockingStore.CountSearchesFor("keep") >= 2);

        blockingStore.ReleaseWrites();
        await pendingWrite;

        for (var attempt = 0; attempt < 200 && viewModel.Clips.Count != 200; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        // All 201 clips still match "keep", so a depth that failed to reset
        // shows up as 201.
        Assert.Equal(200, viewModel.Clips.Count);
    }

    /// <summary>
    /// Regression test for F20: a refresh always re-read a single page from
    /// offset 0, and the incremental diff then removed every row past it. Any
    /// background refresh - a capture, an OCR completion, periodic maintenance -
    /// silently threw away every extra page the user had scrolled in.
    /// Seeds one clip past the 200-row page so real paging is exercised.
    /// </summary>
    [AvaloniaFact]
    public async Task Refresh_KeepsLoadedPages_ButANewSearchResetsThem()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var seedRequests = Enumerable.Range(1, 201)
            .Select(i => new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"keep {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"keep {i}"),
            })
            .ToList();
        var seeded = await scope.ClipStoreService.CaptureBatchAsync(seedRequests);
        Assert.Equal(201, seeded.Imported);

        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService());
        await viewModel.InitializeAsync();

        for (var attempt = 0; attempt < 80 && viewModel.Clips.Count < 200; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(200, viewModel.Clips.Count);

        await viewModel.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(201, viewModel.Clips.Count);

        // Same query: the refresh must re-read to the depth already loaded
        // instead of dropping the row that only the second page brought in.
        await viewModel.RefreshCommand.Execute().ToTask();
        for (var attempt = 0; attempt < 80 && viewModel.Clips.Count != 201; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(201, viewModel.Clips.Count);
        Assert.Equal(201, viewModel.Clips.Select(clip => clip.Id).Distinct().Count());

        // A different query starts over at one page even though 201 rows are
        // loaded and all 201 still match, so the depth cannot ratchet upward.
        viewModel.SearchText = "keep";
        for (var attempt = 0; attempt < 80 && viewModel.Clips.Count != 200; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(200, viewModel.Clips.Count);
    }

    /// <summary>
    /// Regression test for F19: the paging offset was a counter that only the
    /// load paths maintained. Deleting a clip removed it from the list without
    /// adjusting the counter, so once the undo window expired and the row left
    /// the result set, the next page started one row too far in and a clip
    /// disappeared from the history entirely until a full refresh.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadMore_AfterADeleteCommits_DoesNotSkipARow()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        for (var i = 1; i <= 5; i++)
        {
            var seeded = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"clip {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"clip {i}"),
            });
            Assert.NotNull(seeded);
        }

        var pagedStore = new PagedSearchClipStore(scope.ClipStoreService, pageSize: 2);
        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            clipStore: pagedStore);
        await viewModel.InitializeAsync();

        for (var attempt = 0; attempt < 40 && viewModel.Clips.Count < 2; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(2, viewModel.Clips.Count);
        Assert.True(viewModel.HasMoreResults);

        // Delete the newest clip and let the undo window expire, so the row
        // really leaves the result set.
        var firstPage = viewModel.Clips.Select(clip => clip.Id).ToArray();
        viewModel.SelectedClip = viewModel.Clips[0];
        await viewModel.DeleteSelectedCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(viewModel.Clips);
        var survivor = viewModel.Clips[0].Id;
        Assert.DoesNotContain(firstPage[0], viewModel.Clips.Select(clip => clip.Id));

        await viewModel.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        // Four clips remain, and the loaded prefix must stay contiguous: no
        // repeats, no ghost of the deleted clip, and no gap where the clip
        // immediately after the survivor should be.
        var loaded = viewModel.Clips.Select(clip => clip.Id).ToArray();
        Assert.Equal(loaded.Length, loaded.Distinct().Count());
        Assert.DoesNotContain(firstPage[0], loaded);
        Assert.Contains(survivor - 1, loaded);
    }

    /// <summary>
    /// A row hidden because it is pending deletion is still counted by the
    /// query. Comparing the visible rows against that total made the list claim
    /// there was another page to load, which re-fired a full search on every
    /// scroll near the bottom for the whole undo window.
    /// </summary>
    [AvaloniaFact]
    public async Task HasMoreResults_IsFalseWhenOnlyAPendingDeleteIsMissing()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        for (var i = 1; i <= 3; i++)
        {
            await CaptureTextAsync(scope, $"clip {i}");
        }

        // A page large enough to hold every clip: there is genuinely nothing
        // more to load, so any later claim to the contrary is the defect.
        var pagedStore = new PagedSearchClipStore(scope.ClipStoreService, pageSize: 3);
        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            clipStore: pagedStore);
        await viewModel.InitializeAsync();

        for (var attempt = 0; attempt < 40 && viewModel.Clips.Count < 3; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(3, viewModel.Clips.Count);
        Assert.False(viewModel.HasMoreResults);

        // Delete inside the undo window, then refresh while the row is still in
        // the database. Nothing further is loadable, so the flag must stay false.
        viewModel.SelectedClip = viewModel.Clips[0];
        var deleting = viewModel.DeleteSelectedCommand.Execute().ToTask();

        await viewModel.RefreshCommand.Execute().ToTask();
        for (var attempt = 0; attempt < 40 && viewModel.Clips.Count > 2; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(2, viewModel.Clips.Count);
        Assert.False(viewModel.HasMoreResults);

        await deleting;
        Dispatcher.UIThread.RunJobs();
    }
    /// <summary>
    /// The paging offset counts pending deletes so a hidden row does not shift
    /// the next page — but only while the row is genuinely part of the current
    /// result set. Once the user changes the search so the deleted clip no
    /// longer matches, counting it pushes the next page one row too far and
    /// skips a match.
    ///
    /// This guards the design of the offset rather than the original paging
    /// defect: it fails against a naive `Clips.Count + _pendingDeletes.Count`,
    /// not against the pre-fix code.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadMore_AfterFilterStopsMatchingAPendingDelete_DoesNotSkipARow()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        // Five clips that match "keep", then a newest one that does not.
        for (var i = 1; i <= 5; i++)
        {
            await CaptureTextAsync(scope, $"keep {i}");
        }

        await CaptureTextAsync(scope, "zeta only");

        var pagedStore = new PagedSearchClipStore(scope.ClipStoreService, pageSize: 2);
        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            clipStore: pagedStore);
        await viewModel.InitializeAsync();

        for (var attempt = 0; attempt < 40 && viewModel.Clips.Count < 2; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        // Delete the newest clip, then narrow the search so the pending delete
        // is no longer part of the result set at all.
        viewModel.SelectedClip = viewModel.Clips[0];
        var deleting = viewModel.DeleteSelectedCommand.Execute().ToTask();

        viewModel.SearchText = "keep";
        for (var attempt = 0; attempt < 60 && viewModel.Clips.Count < 2; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(2, viewModel.Clips.Count);

        await viewModel.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        var loaded = viewModel.Clips.Select(clip => clip.Id).ToArray();
        Assert.Equal(loaded.Length, loaded.Distinct().Count());
        Assert.Equal(loaded.OrderByDescending(static id => id), loaded);
        Assert.Equal(loaded[0] - loaded.Length + 1, loaded[^1]);

        await deleting;
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task CaptureTextAsync(TemporaryDatabaseScope scope, string text)
    {
        var captured = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
        });
        Assert.NotNull(captured);
    }
    /// <summary>
    /// Delegates to a real store but hands back only the first
    /// <see cref="PageSize"/> rows of each result, while reporting the true
    /// total. That makes HasMoreResults true so paging can be exercised, and
    /// records the offset the view model asks for on each page.
    /// </summary>
    private sealed class PagedSearchClipStore(IClipStoreService inner, int pageSize) : IClipStoreService
    {
        public List<int> RequestedOffsets { get; } = [];

        public async Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default)
        {
            RequestedOffsets.Add(filters.Offset);
            var result = await inner.SearchAsync(filters, cancellationToken);
            return new ClipSearchResult
            {
                Items = result.Items.Take(pageSize).ToList(),
                TotalMatchingCount = result.TotalMatchingCount,
                TotalClipCount = result.TotalClipCount,
                SensitiveClipCount = result.SensitiveClipCount,
                TotalStoredBytes = result.TotalStoredBytes,
                LastCapturedAt = result.LastCapturedAt,
            };
        }

        public Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default) => inner.CaptureBatchAsync(requests, cancellationToken);
        public Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => inner.CaptureAsync(request, cancellationToken);
        public Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => inner.CaptureFastAsync(request, cancellationToken);
        public Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default) => inner.UpdateDeferredContentAsync(clipId, request, cancellationToken);
        public Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default) => inner.UpdateSourceAppIconAsync(clipId, iconBytes, cancellationToken);
        public Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default) => inner.ApplySensitivityAsync(clipId, cancellationToken);
        public Task<int> ApplyPendingSensitivityAsync(CancellationToken cancellationToken = default) => inner.ApplyPendingSensitivityAsync(cancellationToken);
        public Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default) => inner.SetFavoriteAsync(clipId, isFavorite, cancellationToken);
        public Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default) => inner.SetPinnedAsync(clipId, isPinned, cancellationToken);
        public Task DeleteAsync(long clipId, CancellationToken cancellationToken = default) => inner.DeleteAsync(clipId, cancellationToken);
        public Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default) => inner.ClearSensitivityAsync(clipId, cancellationToken);
        public Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default) => inner.SetSensitiveAsync(clipId, isSensitive, cancellationToken);
        public Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkPastedAsync(clipId, cancellationToken);
        public Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default) => inner.TryClaimForOcrAsync(clipId, cancellationToken);
        public Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default) => inner.SetOcrResultAsync(clipId, ocrText, cancellationToken);
        public Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetOcrFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default) => inner.GetPendingOcrClipIdsAsync(cancellationToken);
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkOcrForRerunAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllSucceededForRerunAsync(cancellationToken);
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => inner.GetOcrCoverageAsync(cancellationToken);
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => inner.ApplyMaintenanceAsync(cancellationToken);
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => inner.RebuildSensitivityMatchesAsync(cancellationToken);
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => inner.GetClipAtOffsetAsync(offset, cancellationToken);
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetByIdAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.GetByIdsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => inner.ClaimPendingEmbeddingsAsync(batchSize, cancellationToken);
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => inner.SaveEmbeddingBatchAsync(records, modelVersion, cancellationToken);
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetEmbeddingFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllEmbeddingsForRerunAsync(cancellationToken);
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => inner.GetEmbeddingCoverageAsync(cancellationToken);
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => inner.LoadAllEmbeddingsAsync(cancellationToken);
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => inner.PrewarmAsync(cancellationToken);
    }

    /// <summary>
    /// Records every search the view model issues and can hold a clip write
    /// open, so a test can force the refresh loop down its retry path while a
    /// query change is in flight.
    /// </summary>
    private sealed class BlockingWriteClipStore(IClipStoreService inner) : IClipStoreService
    {
        private readonly List<string> m_searches = [];
        private readonly object m_gate = new();
        private TaskCompletionSource m_release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockWrites { get; set; }

        public bool IsWriteBlocked { get; private set; }

        public int CountSearchesFor(string searchText)
        {
            lock (m_gate)
            {
                return m_searches.Count(text => string.Equals(text, searchText, StringComparison.Ordinal));
            }
        }

        public void ReleaseWrites()
        {
            BlockWrites = false;
            m_release.TrySetResult();
        }

        public Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default)
        {
            lock (m_gate)
            {
                m_searches.Add(filters.SearchText ?? string.Empty);
            }

            return inner.SearchAsync(filters, cancellationToken);
        }

        public async Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default)
        {
            if (BlockWrites)
            {
                IsWriteBlocked = true;
                await m_release.Task;
                IsWriteBlocked = false;
            }

            await inner.SetFavoriteAsync(clipId, isFavorite, cancellationToken);
        }
        public Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default) => inner.CaptureBatchAsync(requests, cancellationToken);
        public Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => inner.CaptureAsync(request, cancellationToken);
        public Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => inner.CaptureFastAsync(request, cancellationToken);
        public Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default) => inner.UpdateDeferredContentAsync(clipId, request, cancellationToken);
        public Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default) => inner.UpdateSourceAppIconAsync(clipId, iconBytes, cancellationToken);
        public Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default) => inner.ApplySensitivityAsync(clipId, cancellationToken);
        public Task<int> ApplyPendingSensitivityAsync(CancellationToken cancellationToken = default) => inner.ApplyPendingSensitivityAsync(cancellationToken);
        public Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default) => inner.SetPinnedAsync(clipId, isPinned, cancellationToken);
        public Task DeleteAsync(long clipId, CancellationToken cancellationToken = default) => inner.DeleteAsync(clipId, cancellationToken);
        public Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default) => inner.ClearSensitivityAsync(clipId, cancellationToken);
        public Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default) => inner.SetSensitiveAsync(clipId, isSensitive, cancellationToken);
        public Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkPastedAsync(clipId, cancellationToken);
        public Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default) => inner.TryClaimForOcrAsync(clipId, cancellationToken);
        public Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default) => inner.SetOcrResultAsync(clipId, ocrText, cancellationToken);
        public Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetOcrFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default) => inner.GetPendingOcrClipIdsAsync(cancellationToken);
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkOcrForRerunAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllSucceededForRerunAsync(cancellationToken);
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => inner.GetOcrCoverageAsync(cancellationToken);
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => inner.ApplyMaintenanceAsync(cancellationToken);
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => inner.RebuildSensitivityMatchesAsync(cancellationToken);
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => inner.GetClipAtOffsetAsync(offset, cancellationToken);
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetByIdAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.GetByIdsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => inner.ClaimPendingEmbeddingsAsync(batchSize, cancellationToken);
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => inner.SaveEmbeddingBatchAsync(records, modelVersion, cancellationToken);
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetEmbeddingFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllEmbeddingsForRerunAsync(cancellationToken);
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => inner.GetEmbeddingCoverageAsync(cancellationToken);
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => inner.LoadAllEmbeddingsAsync(cancellationToken);
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => inner.PrewarmAsync(cancellationToken);
    }
    private static async Task PrepareInitializedScopeAsync(TemporaryDatabaseScope scope)
    {
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);
        await scope.DatabaseInitializer.InitializeAsync();
    }

    [AvaloniaFact]
    public async Task CorruptedDatabase_SurfacesStartupErrorOverlay()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);

        // First, create a real, working SQLite database so the header is
        // valid (RequiresPassword check passes), then corrupt one of the
        // non-header pages so PRAGMA quick_check inside DatabaseInitializer
        // fails. This is the scenario today's incident produced and the one
        // the startup-error overlay is meant to cover.
        using (var seed = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = scope.DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
        }.ToString()))
        {
            seed.Open();
            using var cmd = seed.CreateCommand();
            cmd.CommandText = "CREATE TABLE widgets (id INTEGER PRIMARY KEY, label TEXT); " +
                              // Insert enough rows to definitely spill past page 1 (~4 KB).
                              "WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n < 400) " +
                              "INSERT INTO widgets (label) SELECT printf('row-%04d-%s', n, hex(randomblob(40))) FROM seq;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Zero out a chunk of the file past the header but inside the first
        // table b-tree page. That preserves the SQLite magic (so the file
        // still looks like a database to RequiresPassword) but breaks the
        // pages quick_check walks.
        var dbBytes = await System.IO.File.ReadAllBytesAsync(scope.DatabasePath);
        Assert.True(dbBytes.Length > 8192, "Seeded database must be at least two pages.");
        for (var offset = 4096; offset < 4096 + 512; offset++)
        {
            dbBytes[offset] = 0xFF;
        }
        await System.IO.File.WriteAllBytesAsync(scope.DatabasePath, dbBytes);

        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        // Initialize kicks off StartDatabaseInBackgroundAsync as fire-and-
        // forget. Spin briefly until the background task either succeeds
        // (test setup wrong) or fails through to HasStartupError.
        for (var attempt = 0; attempt < 50 && !viewModel.HasStartupError; attempt++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(viewModel.HasStartupError, "Corrupted database should set HasStartupError.");
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StartupErrorMessage),
            "StartupErrorMessage should describe the failure for the user.");
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StartupErrorTitle),
            "StartupErrorTitle should be set so the overlay has a heading.");
        Assert.False(viewModel.IsLoadingDatabase,
            "Loading overlay must close so the error overlay can replace it.");
    }

    private static async Task<ClipEntry> CaptureTextClipAsync(IClipStoreService clipStoreService, string text)
    {
        return (await clipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = Encoding.UTF8.GetBytes(text),
            ContentText = text,
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = "Tests"
        }))!;
    }

    private static async Task<ClipEntry> CaptureRichTextClipAsync(IClipStoreService clipStoreService, string markup, ClipContentFormat format)
    {
        return (await clipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = Encoding.UTF8.GetBytes(markup),
            ContentText = ClipDisplayFormatter.RenderRichContent(markup),
            ContentType = ContentType.RichText,
            ContentFormat = format,
            SourceApp = "Tests"
        }))!;
    }

    private static async Task<ClipEntry> CaptureFilesClipAsync(IClipStoreService clipStoreService, IReadOnlyList<string> paths)
    {
        var text = string.Join(Environment.NewLine, paths);
        return (await clipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = Encoding.UTF8.GetBytes(text),
            ContentText = text,
            ContentType = ContentType.Files,
            ContentFormat = ClipContentFormat.FileList,
            SourceApp = "Tests"
        }))!;
    }

    private static async Task<ClipEntry> CaptureImageClipAsync(IClipStoreService clipStoreService, byte[] bytes, string label)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var bitmap = new Bitmap(stream);
        return (await clipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = bytes,
            ContentText = label,
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ImageWidth = bitmap.PixelSize.Width,
            ImageHeight = bitmap.PixelSize.Height,
            SourceApp = "Tests"
        }))!;
    }

    private static ClipEntry CreateImageClipEntry(byte[] bytes, string label)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var bitmap = new Bitmap(stream);
        return new ClipEntry
        {
            Id = 42,
            Content = label,
            ContentBytes = bytes,
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            SourceApp = "Tests",
            Hash = Guid.NewGuid().ToString("N"),
            ByteSize = bytes.LongLength,
            ImageWidth = bitmap.PixelSize.Width,
            ImageHeight = bitmap.PixelSize.Height,
        };
    }

    [AvaloniaFact]
    public async Task ImageClip_ShowsImageRenderer_RegardlessOfRawToggle()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        var pngBytes = CreatePngBytes(0);
        var clip = await CaptureImageClipAsync(scope.ClipStoreService, pngBytes, "test image");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(viewModel.SelectedClip);
        Assert.True(viewModel.ShowSelectedImageRenderer);
        Assert.False(viewModel.ShowRawTextContent);

        viewModel.SelectedContentDisplayMode = ContentDisplayMode.Raw;
        Assert.True(viewModel.ShowSelectedImageRenderer, "Image should remain visible when Raw is toggled on");
        Assert.False(viewModel.ShowRawTextContent, "Raw text should not show for images");

        viewModel.SelectedContentDisplayMode = ContentDisplayMode.Rendered;
        Assert.True(viewModel.ShowSelectedImageRenderer, "Image should remain visible when Raw is toggled off");
    }

    [AvaloniaFact]
    public async Task ImageClip_RawToggle_IsNotApplicable()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        var pngBytes = CreatePngBytes(0);
        var clip = await CaptureImageClipAsync(scope.ClipStoreService, pngBytes, "test image");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(viewModel.SelectedClip);
        Assert.False(viewModel.IsDisplayModeApplicable, "Display mode selector should not be applicable for image clips");
    }

    [AvaloniaFact]
    public async Task TextClip_AlwaysUsesRawEditor()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "hello world");
        clipboardMonitor.Emit(clip);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(viewModel.SelectedClip);
        // Plain text clips always use the raw text editor; the rendered text
        // editor and the Raw toggle are not applicable.
        Assert.False(viewModel.ShowSelectedTextRenderer, "Rendered text editor should not be used for text clips");
        Assert.True(viewModel.ShowRawTextContent, "Raw text should always show for text clips");
        Assert.False(viewModel.IsDisplayModeApplicable, "Display mode selector should not be applicable for text clips");
        Assert.Contains("hello world", viewModel.EditedClipText);
    }

    private static byte[] CreatePngBytes(int bgraColor)
    {
        _ = bgraColor;
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");
    }
}