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
            new Clipthrough.Services.ScriptingService(),
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
    public async Task CopyEditedClip_CopiesModifiedText()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

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
            EnableRemoteApi = true,
            RemoteApiPort = 12345,
            RemoteApiToken = "tok-char",
            RemoteApiBindAddress = "127.0.0.1",
            ThemeMode = Models.ThemeMode.Light,
            CloseToTray = true,
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
        // Remote-API section
        Assert.True(viewModel.Settings.EnableRemoteApi);
        Assert.Equal(12345, viewModel.Settings.RemoteApiPort);
        Assert.Equal("tok-char", viewModel.Settings.RemoteApiToken);
        Assert.Equal("127.0.0.1", viewModel.Settings.RemoteApiBindAddress);
        // Theme / misc
        Assert.Equal(Models.ThemeMode.Light, viewModel.Settings.ThemeMode);
        Assert.True(viewModel.Settings.CloseToTray);
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
        viewModel.SelectedContentTypeOption = viewModel.ContentTypeOptions[2];

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
        Assert.Equal(ContentType.Image, scope.SettingsService.Current.LastContentTypeFilter);
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

    private static MainWindowViewModel CreateViewModel(
        TemporaryDatabaseScope scope,
        TestClipboardMonitorService clipboardMonitor,
        TestSystemInteractionService systemInteraction,
        TestSessionLogService sessionLogService,
        TestImageEditorService? imageEditorService = null)
    {
        return new MainWindowViewModel(
            scope.ClipStoreService,
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
            new Clipthrough.Services.ScriptingService(),
            new TestOcrService(),
            new NoOpBackgroundOcrQueue(),
            new Clipthrough.Services.BackgroundJobIndicator(),
            scope.DatabaseInitializer);
    }

    private sealed class NoOpBackgroundOcrQueue : Clipthrough.Services.IBackgroundOcrQueue
    {
        public IObservable<long> OcrCompleted { get; } = System.Reactive.Linq.Observable.Empty<long>();
        public IObservable<System.Reactive.Unit> QueueChanged { get; } = System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();
        public void Start() { }
        public Task StopAsync() => Task.CompletedTask;
        public void Enqueue(long clipId) { }
        public Task EnqueueBacklogAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
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
