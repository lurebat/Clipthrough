using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
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
    public async Task SelectedClipFiles_AreProbedOnlyForFileClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var missingPath = Path.Combine(Path.GetTempPath(), "clipthrough-absent-" + Guid.NewGuid().ToString("N"));

        var textClip = await CaptureTextClipAsync(scope.ClipStoreService, missingPath);
        clipboardMonitor.Emit(textClip);
        for (var attempt = 0; attempt < 20 && viewModel.SelectedClip?.Id != textClip.Id; attempt++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        Assert.True(
            viewModel.SelectedClipFiles.Single().Exists,
            "a text clip must not be probed, so availability stays at its optimistic default");

        var filesClip = await CaptureFilesClipAsync(scope.ClipStoreService, [missingPath]);
        clipboardMonitor.Emit(filesClip);
        for (var attempt = 0; attempt < 40 && viewModel.SelectedClipFiles.FirstOrDefault()?.Exists != false; attempt++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.False(viewModel.SelectedClipFiles.Single().Exists);
    }

    /// <summary>
    /// BuildFileItems splits any clip into lines, so a text clip produces "file items"
    /// that are not paths. Probing those would put a disk hit - and for anything with a
    /// UNC prefix, a multi-second network timeout - behind selecting a block of text.
    /// </summary>
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

    /// <summary>
    /// Rendered rich text is read-only for HTML too, now that it renders natively rather
    /// than in a WebView. This test asserted the opposite while the WebView hosted a
    /// contenteditable surface; it is inverted rather than deleted, because the change is
    /// deliberate and the old behaviour must not creep back unnoticed.
    /// </summary>
    [AvaloniaFact]
    public async Task RichContent_RenderedMode_IsReadOnlyForHtml()
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
        Assert.False(viewModel.CanEditSelectedRichTextInRenderedMode);
        Assert.False(viewModel.ShowCopyEditedClipButton);
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

    /// <summary>
    /// The rendered pane no longer offers "copy as new" for HTML, because it no longer
    /// hosts an editor. Editing HTML is still reachable through the Textual and Raw panes,
    /// which are unchanged, so this asserts the button is absent rather than that editing
    /// is impossible.
    /// </summary>
    [AvaloniaFact]
    public async Task HtmlRichContent_RenderedMode_DoesNotOfferCopyAsNewEditing()
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

        Assert.True(viewModel.ShowSelectedRichTextRenderer);
        Assert.False(viewModel.CanEditSelectedRichTextInRenderedMode);
        Assert.False(viewModel.ShowCopyEditedClipButton);

        // Anti-vacuity: the same clip is still editable through the textual pane, so this
        // is asserting that the *rendered* pane declines, not that the clip is read-only.
        viewModel.SelectedContentDisplayMode = ContentDisplayMode.Textual;
        Assert.True(viewModel.IsSelectedClipTextEditable);
        Assert.True(viewModel.ShowCopyEditedClipButton);
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

        // The production settings service writes settings.json and a legacy
        // SQLite copy, so its save only completes after several awaits. Disposal
        // has to wait for it; with a save that completes on the caller's stack
        // this test would pass even for a fire-and-forget flush.
        var saveDelay = TimeSpan.FromMilliseconds(150);
        scope.SettingsService.SaveDelay = saveDelay;

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        viewModel.Dispose();
        elapsed.Stop();

        // Asserted separately from the values below: it is the part that proves
        // disposal blocked on the save rather than merely observing a fake that
        // happened to have finished already.
        Assert.True(
            elapsed.Elapsed >= saveDelay - TimeSpan.FromMilliseconds(50),
            $"Dispose returned after {elapsed.ElapsedMilliseconds} ms without waiting for the {saveDelay.TotalMilliseconds} ms save.");

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
    public async Task SaveSettings_WithChangedSensitivityRules_ReloadsSemanticCacheAndPokesEmbeddingWorker()
    {
        // A rule change moves clips into and out of embedding eligibility. The
        // semantic cache is a snapshot taken under the old rules, so without a
        // reload a clip the user just made sensitive stays semantically
        // searchable for the rest of the session.
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        var semantic = new StubSemanticSearchService();
        var embeddingWorker = new RecordingEmbeddingWorker();
        using var viewModel = CreateViewModel(
            scope, clipboardMonitor, systemInteraction, sessionLogService,
            semanticSearchService: semantic, embeddingWorker: embeddingWorker);

        await viewModel.InitializeAsync();

        // InitializeAsync kicks the database open on a worker thread and returns.
        // SaveSettingsAsync short-circuits to "still loading" until that lands, so
        // the rebuild would never run.
        for (var attempt = 0; attempt < 100 && viewModel.IsLoadingDatabase; attempt++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(viewModel.IsLoadingDatabase);

        var refreshesBefore = semantic.RefreshCount;
        var pokesBefore = embeddingWorker.PokeCount;

        viewModel.AddSensitivityRuleCommand.Execute().Subscribe();
        var added = viewModel.SensitivityRules[^1];
        added.Name = "k8 rule";
        added.Pattern = "k8-secret-token";
        added.IsEnabled = true;

        await viewModel.SaveSettingsCommand.Execute().ToTask();

        for (var attempt = 0; attempt < 50 && (semantic.RefreshCount == refreshesBefore || embeddingWorker.PokeCount == pokesBefore); attempt++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(semantic.RefreshCount > refreshesBefore, "Expected the semantic cache to be reloaded after a sensitivity rule change.");
        Assert.True(embeddingWorker.PokeCount > pokesBefore, "Expected the embedding worker to be poked after a sensitivity rule change.");
    }

    /// <summary>
    /// The window handles Ctrl+D itself ("copy selected") before the
    /// configurable filter hotkeys get a look, so assigning it to a filter
    /// produces a shortcut that silently never fires. Validation has to say so
    /// rather than save a dead binding.
    /// </summary>
    [AvaloniaFact]
    public async Task SaveSettings_RefusesAFilterHotkeyTheWindowHandlesItself()
    {
        using var scope = new TemporaryDatabaseScope();
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        viewModel.Settings.EnableToggleFavoritesHotkey = true;
        viewModel.Settings.ToggleFavoritesHotkey = "Ctrl+D";

        await viewModel.SaveSettingsCommand.Execute().ToTask();

        Assert.Contains("Ctrl+D", viewModel.StatusText ?? string.Empty, StringComparison.Ordinal);
        Assert.False(scope.SettingsService.HasSavedSettings);
    }

    /// <summary>
    /// The exclusion list is edited as free text and stored as a list, so both
    /// halves of that translation have to be wired up. A save that dropped the
    /// text on the floor would leave the user believing an app is excluded
    /// while every one of its copies is still captured.
    /// </summary>
    [AvaloniaFact]
    public async Task SaveSettings_PersistsTheCaptureExclusionList()
    {
        using var scope = new TemporaryDatabaseScope();
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        viewModel.Settings.ExcludedCaptureAppsText = "1Password\n\n  *keepass*  \n";
        await viewModel.SaveSettingsCommand.Execute().ToTask();

        Assert.Equal(new[] { "1Password", "*keepass*" }, scope.SettingsService.Current.ExcludedCaptureApps);

        // And the round trip back into the form. CloseSettings reloads the
        // draft from the saved settings, so a missing load would leave the box
        // empty and the next save would silently wipe the list.
        viewModel.Settings.ExcludedCaptureAppsText = "scratched out";
        viewModel.CloseSettingsCommand.Execute().Subscribe();
        Assert.Equal(
            new[] { "1Password", "*keepass*" },
            Clipthrough.Services.CaptureExclusionPolicy.ParsePatterns(viewModel.Settings.ExcludedCaptureAppsText));
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

    /// <summary>
    /// Semantic fusion contributes clips the SQL query did not return, ranked
    /// globally from rank 0. Handing that same global top-K to every paged read
    /// meant "Load more" re-added the same clips it had already shown, giving a
    /// clip two view models that no longer shared selection or checkbox state.
    /// The offset was also advanced past them, so genuine query matches were
    /// skipped and never appeared at all.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadMore_WithSemanticFusion_AddsNoDuplicatesAndSkipsNoQueryRows()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        // Five clips the text query matches, plus one it does not.
        for (var i = 1; i <= 5; i++)
        {
            await CaptureTextAsync(scope, $"alpha {i}");
        }

        await CaptureTextAsync(scope, "unrelated wording");
        var semanticOnly = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = "unrelated" });
        var semanticOnlyId = Assert.Single(semanticOnly.Items).Id;

        var pagedStore = new PagedSearchClipStore(scope.ClipStoreService, pageSize: 2);
        var semantic = new StubSemanticSearchService(semanticOnlyId);
        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            clipStore: pagedStore,
            semanticSearchService: semantic);
        await viewModel.InitializeAsync();

        viewModel.UseSemanticClipSearch = true;
        viewModel.SearchText = "alpha";

        // Two query rows plus the semantic addition.
        for (var attempt = 0; attempt < 200 && viewModel.Clips.Count != 3; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(3, viewModel.Clips.Count);
        Assert.Contains(semanticOnlyId, viewModel.Clips.Select(clip => clip.Id));

        var queriesBeforePaging = semantic.QueryCount;
        Assert.True(queriesBeforePaging > 0, "the first page should have consulted semantic ranking");

        pagedStore.RequestedOffsets.Clear();
        await viewModel.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        // Semantic ranking is global and starts at rank 0, so it says nothing
        // about an offset window. A paged read must not consult it at all -
        // doing so both costs a full vector scan per page and hands the page the
        // same hits it already showed.
        Assert.Equal(queriesBeforePaging, semantic.QueryCount);

        // The next page resumes after the two rows the query returned, not after
        // the three rows on screen - offset 3 would skip a matching clip.
        Assert.Equal([2], pagedStore.RequestedOffsets);

        var loaded = viewModel.Clips.Select(clip => clip.Id).ToArray();
        Assert.Equal(loaded.Length, loaded.Distinct().Count());
        Assert.Single(loaded, id => id == semanticOnlyId);

        // Every "alpha" clip read so far is present: four query rows over two
        // pages, plus the semantic addition.
        Assert.Equal(5, loaded.Length);
    }

    /// <summary>
    /// A clip surfaced early by semantic fusion can also be a genuine query
    /// match that a later page reaches. Once the query returns it, it occupies a
    /// slot in the offset space: continuing to treat it as an addition holds the
    /// offset one row short forever, so every further "Load more" re-reads the
    /// same row, discards it as a duplicate, and adds nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadMore_WhenAFusedClipIsAlsoAQueryMatch_KeepsMakingProgress()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        for (var i = 1; i <= 6; i++)
        {
            await CaptureTextAsync(scope, $"alpha {i}");
        }

        // The oldest clip: a real query match that lands on the last page, and
        // also the one the semantic ranking pulls to the front.
        var all = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = "alpha" });
        Assert.Equal(6, all.Items.Count);
        var oldestId = all.Items[^1].Id;

        var pagedStore = new PagedSearchClipStore(scope.ClipStoreService, pageSize: 2);
        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            clipStore: pagedStore,
            semanticSearchService: new StubSemanticSearchService(oldestId));
        await viewModel.InitializeAsync();

        viewModel.UseSemanticClipSearch = true;
        viewModel.SearchText = "alpha";

        for (var attempt = 0; attempt < 200 && viewModel.Clips.Count != 3; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(3, viewModel.Clips.Count);
        Assert.Contains(oldestId, viewModel.Clips.Select(clip => clip.Id));

        // Page until the list stops growing, then check it really is finished
        // rather than stuck re-reading a row it already has.
        var offsets = new List<int>();
        for (var page = 0; page < 6 && viewModel.HasMoreResults; page++)
        {
            var before = viewModel.Clips.Count;
            pagedStore.RequestedOffsets.Clear();
            await viewModel.LoadMoreCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();
            offsets.AddRange(pagedStore.RequestedOffsets);
            if (viewModel.Clips.Count == before)
            {
                break;
            }
        }

        var loaded = viewModel.Clips.Select(clip => clip.Id).ToArray();
        Assert.Equal(loaded.Length, loaded.Distinct().Count());
        Assert.Equal(6, loaded.Length);
        Assert.False(viewModel.HasMoreResults);

        // Each page has to move forward; a repeated offset is the stall.
        Assert.Equal(offsets.Count, offsets.Distinct().Count());
    }

    /// <summary>
    /// Fusion used to trim the merged list back to the page size, which dropped
    /// the lowest-ranked query rows to make room for the semantic additions. The
    /// next page then resumed past those rows, so clips that matched the search
    /// were never shown on any page. Semantic fusion adds recall; it must not
    /// cost any.
    /// </summary>
    [AvaloniaFact]
    public async Task Refresh_WithSemanticFusion_KeepsEveryQueryRowOfThePage()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        // A full page of matches (the view model asks for 200) plus one clip the
        // text query does not match.
        var seedRequests = Enumerable.Range(1, 200)
            .Select(i => new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"alpha {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"alpha {i}"),
            })
            .ToList();
        Assert.Equal(200, (await scope.ClipStoreService.CaptureBatchAsync(seedRequests)).Imported);

        await CaptureTextAsync(scope, "unrelated wording");
        var semanticOnly = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = "unrelated" });
        var semanticOnlyId = Assert.Single(semanticOnly.Items).Id;

        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            semanticSearchService: new StubSemanticSearchService(semanticOnlyId));
        await viewModel.InitializeAsync();

        viewModel.UseSemanticClipSearch = true;
        viewModel.SearchText = "alpha";

        for (var attempt = 0; attempt < 200 && viewModel.Clips.Count != 201; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        // All 200 query rows survive alongside the semantic addition.
        Assert.Equal(201, viewModel.Clips.Count);
        Assert.Contains(semanticOnlyId, viewModel.Clips.Select(clip => clip.Id));
        Assert.Equal(200, viewModel.Clips.Count(clip => clip.Id != semanticOnlyId));
    }

    // K3: the EnableSemanticSearch setting was read exactly once, in
    // StartBackgroundServices. Toggling it in Settings changed nothing until the
    // next launch — turning it on left every clip unembedded (so semantic search
    // silently returned nothing) and turning it off left the worker burning CPU.
    [AvaloniaFact]
    public async Task SemanticSearchSetting_TogglingIt_StartsAndStopsTheEmbeddingWorker()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(AppSettings.Default with { EnableSemanticSearch = false });

        var worker = new RecordingEmbeddingWorker();
        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            embeddingWorker: worker);
        await viewModel.InitializeAsync();

        viewModel.StartBackgroundServices();
        Assert.False(worker.IsRunning);
        Assert.Equal(0, worker.StartCount);

        scope.SettingsService.SetCurrent(scope.SettingsService.Current with { EnableSemanticSearch = true });
        await viewModel.SemanticWorkerTransition;
        Assert.True(worker.IsRunning);
        Assert.Equal(1, worker.StartCount);

        scope.SettingsService.SetCurrent(scope.SettingsService.Current with { EnableSemanticSearch = false });
        await viewModel.SemanticWorkerTransition;
        Assert.False(worker.IsRunning);
        Assert.Equal(1, worker.StopCount);
    }

    // A rapid off -> on toggle must not let the pending stop overwrite the start:
    // transitions are chained, so the final worker state always matches the final
    // setting value rather than whichever call happened to finish last.
    [AvaloniaFact]
    public async Task SemanticSearchSetting_ToggledOffThenOnQuickly_LeavesTheWorkerRunning()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(AppSettings.Default with { EnableSemanticSearch = true });

        var worker = new RecordingEmbeddingWorker();
        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            embeddingWorker: worker);
        await viewModel.InitializeAsync();

        viewModel.StartBackgroundServices();
        Assert.True(worker.IsRunning);

        // Repeated, and settled before asserting. Awaiting the transition is only
        // conclusive because the transitions are chained: the last task cannot
        // complete until the previous one has. Run them unchained and both are in
        // flight at once, so awaiting the last says nothing about the other, and
        // whether the stop lands after the start is left to the scheduler. One
        // pass therefore agrees with a broken implementation about half the time,
        // which is how this stayed green against exactly that.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var stopsBefore = worker.StopCount;

            scope.SettingsService.SetCurrent(scope.SettingsService.Current with { EnableSemanticSearch = false });
            scope.SettingsService.SetCurrent(scope.SettingsService.Current with { EnableSemanticSearch = true });
            await viewModel.SemanticWorkerTransition;

            // Give anything still pending room to land on top of the result.
            await Task.Delay(80);
            Dispatcher.UIThread.RunJobs();

            Assert.True(
                worker.IsRunning,
                $"attempt {attempt}: the worker is stopped while semantic search is on, so a pending stop overtook the start");
            Assert.Equal(stopsBefore + 1, worker.StopCount);
        }

        Assert.Equal(11, worker.StartCount);
    }

    // A7: startup is a long chain of awaits and nothing cancels it, so quitting
    // partway through used to let StartBackgroundServices run after shutdown had
    // already stopped everything - restarting the clipboard monitor, the OCR
    // queue and the embedding worker as the process was tearing down, with the
    // workers writing to a database on its way out.
    [AvaloniaFact]
    public async Task StartBackgroundServices_AfterDispose_StartsNothing()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(AppSettings.Default with { EnableSemanticSearch = true });

        var clipboardMonitor = new TestClipboardMonitorService();
        var worker = new RecordingEmbeddingWorker();
        var viewModel = CreateViewModel(
            scope,
            clipboardMonitor,
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            embeddingWorker: worker);
        await viewModel.InitializeAsync();

        viewModel.Dispose();
        viewModel.StartBackgroundServices();

        Assert.False(clipboardMonitor.IsRunning);
        Assert.False(worker.IsRunning);
        Assert.Equal(0, worker.StartCount);
    }

    // The same call on a live view model must still start everything, so the
    // guard above cannot pass by disabling startup altogether.
    [AvaloniaFact]
    public async Task StartBackgroundServices_BeforeDispose_StartsEverything()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(AppSettings.Default with { EnableSemanticSearch = true });

        var clipboardMonitor = new TestClipboardMonitorService();
        var worker = new RecordingEmbeddingWorker();
        using var viewModel = CreateViewModel(
            scope,
            clipboardMonitor,
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            embeddingWorker: worker);
        await viewModel.InitializeAsync();

        viewModel.StartBackgroundServices();

        Assert.True(clipboardMonitor.IsRunning);
        Assert.True(worker.IsRunning);
        Assert.Equal(1, worker.StartCount);
    }

    // Dispose runs from Window.Closed and blocks on a settings flush. Letting it
    // run twice would pay that wait twice on the way out.
    [AvaloniaFact]
    public async Task Dispose_IsIdempotent()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService());
        await viewModel.InitializeAsync();

        viewModel.Dispose();
        var savesAfterFirst = scope.SettingsService.SaveCallCount;
        viewModel.Dispose();

        Assert.Equal(savesAfterFirst, scope.SettingsService.SaveCallCount);
    }

    /// <summary>
    /// Icons exist for nearly every clip and list reads omit the blob (U12), so every
    /// visible row asks for its icon back the moment it renders. Each of those used to be
    /// a full thirty-column read - image blob included - on its own connection. The whole
    /// page renders in one pass, so nothing is complete when the second row asks: the
    /// sharing has to happen on the in-flight read, not on its result.
    /// </summary>
    [AvaloniaFact]
    public async Task SourceAppIcons_AreFetchedOncePerApplication_NotOncePerRow()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        var iconBytes = new byte[128];
        new Random(11).NextBytes(iconBytes);

        const int rows = 12;
        for (var i = 0; i < rows; i++)
        {
            var seeded = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"shared app clip {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"shared app clip {i}"),
                SourceApp = "Editor",
                SourceAppPath = @"C:\apps\editor.exe",
                SourceAppIconBytes = iconBytes,
            });
            Assert.NotNull(seeded);
        }

        var countingStore = new IconCountingClipStore(scope.ClipStoreService);
        using var viewModel = CreateViewModel(
            scope,
            new TestClipboardMonitorService(),
            new TestSystemInteractionService(),
            new TestSessionLogService(),
            clipStore: countingStore);
        await viewModel.InitializeAsync();

        for (var attempt = 0; attempt < 40 && viewModel.Clips.Count < rows; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(rows, viewModel.Clips.Count);

        // Render every row at once, the way the list itself does.
        foreach (var clip in viewModel.Clips)
        {
            _ = clip.SourceAppIconImage;
        }

        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (viewModel.Clips.All(c => c.SourceAppIconBytes is not null))
            {
                break;
            }
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        // Anti-vacuity: the icons really did arrive, so a count of one means sharing
        // rather than a load path that never ran.
        Assert.All(viewModel.Clips, c => Assert.Equal(iconBytes, c.SourceAppIconBytes));
        Assert.Equal(1, countingStore.IconReads);

        // And none of it went through the full-row read it used to.
        Assert.Equal(0, countingStore.FullRowReads);
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
        IClipStoreService? clipStore = null,
        Clipthrough.Services.Search.ISemanticSearchService? semanticSearchService = null,
        Clipthrough.Services.Search.IEmbeddingWorker? embeddingWorker = null)
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
            semanticSearchService: semanticSearchService,
            embeddingWorker: embeddingWorker,
            dragDropService: dragDropService);
    }

    /// <summary>
    /// Records start/stop calls and tracks the running state the way the real
    /// worker does, so lifecycle wiring can be asserted.
    /// </summary>
    private sealed class RecordingEmbeddingWorker : Clipthrough.Services.Search.IEmbeddingWorker
    {
        private int _startCount;
        private int _stopCount;
        private int _pokeCount;
        private int _isRunning;

        public int StartCount => Volatile.Read(ref _startCount);

        public int StopCount => Volatile.Read(ref _stopCount);

        public int PokeCount => Volatile.Read(ref _pokeCount);

        public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

        public IObservable<int> BatchCompleted { get; } = System.Reactive.Linq.Observable.Empty<int>();

        public IObservable<IReadOnlyList<ClipEmbeddingRecord>> BatchRecordsCompleted { get; } =
            System.Reactive.Linq.Observable.Empty<IReadOnlyList<ClipEmbeddingRecord>>();

        public void Start()
        {
            Interlocked.Increment(ref _startCount);
            Volatile.Write(ref _isRunning, 1);
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCount);
            Volatile.Write(ref _isRunning, 0);
            return Task.CompletedTask;
        }

        public void Poke() => Interlocked.Increment(ref _pokeCount);

        public Task<EmbeddingCoverage> GetCoverageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbeddingCoverage(0, 0, 0, 0, 0));

        public Task RerunAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Returns a caller-supplied ranking for every query, so a test can decide
    /// exactly which clips semantic fusion contributes.
    /// </summary>
    private sealed class StubSemanticSearchService(params long[] clipIds) : Clipthrough.Services.Search.ISemanticSearchService
    {
        public System.Threading.Tasks.Task RemoveEmbeddingsAsync(System.Collections.Generic.IReadOnlyList<long> ids, System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.CompletedTask;

        private int _queryCount;
        private int _refreshCount;

        public bool IsAvailable => true;

        public int CachedCount => clipIds.Length;

        /// <summary>Queries run so far. Incremented from the background search thread.</summary>
        public int QueryCount => Volatile.Read(ref _queryCount);

        /// <summary>Cache reloads requested so far.</summary>
        public int RefreshCount => Volatile.Read(ref _refreshCount);

        public Task RefreshCacheAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _refreshCount);
            return Task.CompletedTask;
        }

        public Task AppendEmbeddingsAsync(IReadOnlyList<ClipEmbeddingRecord> records, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<(long ClipId, float Score)>> QueryAsync(string text, int topK, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _queryCount);
            IReadOnlyList<(long, float)> hits = clipIds
                .Take(topK)
                .Select((id, index) => (id, 1.0f - (index * 0.01f)))
                .ToList();
            return Task.FromResult(hits);
        }
    }

    private sealed class NoOpBackgroundOcrQueue : Clipthrough.Services.IBackgroundOcrQueue
    {
        private readonly List<long> _enqueued = [];

        public IReadOnlyList<long> Enqueued => _enqueued;

        public IObservable<long> OcrCompleted { get; } = System.Reactive.Linq.Observable.Empty<long>();
        public IObservable<System.Reactive.Unit> QueueChanged { get; } = System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();
        public bool IsRunning { get; private set; }
        public void Start() { IsRunning = true; }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
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
        public System.IObservable<System.Collections.Generic.IReadOnlyList<long>> ClipsRemoved => System.Reactive.Linq.Observable.Never<System.Collections.Generic.IReadOnlyList<long>>();

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

        public Task<int> ResetStalledOcrClaimsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OcrCoverage(0, 0, 0, 0, 0));
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ClipMaintenanceResult());
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]?> GetSourceAppIconAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
        public Task<int> ResetStalledEmbeddingClaimsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Wraps a real store and can park a single <see cref="SearchAsync"/> call
    /// after it has read the database but before the caller applies the result.
    /// That is the exact window in which a clip write turns a refresh snapshot
    /// stale.
    /// </summary>
    /// <summary>
    /// Counts source-app icon reads so a test can prove the list is not issuing one per row.
    /// </summary>
    /// <summary>
    /// Counts source-app icon reads so a test can prove the list is not issuing one per row.
    /// </summary>
    private sealed class IconCountingClipStore(IClipStoreService inner) : IClipStoreService
    {
        public System.IObservable<System.Collections.Generic.IReadOnlyList<long>> ClipsRemoved => System.Reactive.Linq.Observable.Never<System.Collections.Generic.IReadOnlyList<long>>();

        private int _iconReads;
        private int _fullRowReads;

        public int IconReads => Volatile.Read(ref _iconReads);
        public int FullRowReads => Volatile.Read(ref _fullRowReads);

        public Task<byte[]?> GetSourceAppIconAsync(long clipId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _iconReads);
            return inner.GetSourceAppIconAsync(clipId, cancellationToken);
        }

        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _fullRowReads);
            return inner.GetByIdAsync(clipId, cancellationToken);
        }

        public Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default) => inner.SearchAsync(filters, cancellationToken);

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
        public Task<int> ResetStalledOcrClaimsAsync(CancellationToken cancellationToken = default) => inner.ResetStalledOcrClaimsAsync(cancellationToken);
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkOcrForRerunAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllSucceededForRerunAsync(cancellationToken);
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => inner.GetOcrCoverageAsync(cancellationToken);
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => inner.ApplyMaintenanceAsync(cancellationToken);
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => inner.RebuildSensitivityMatchesAsync(cancellationToken);
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => inner.GetClipAtOffsetAsync(offset, cancellationToken);
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.GetByIdsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => inner.ClaimPendingEmbeddingsAsync(batchSize, cancellationToken);
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => inner.SaveEmbeddingBatchAsync(records, modelVersion, cancellationToken);
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetEmbeddingFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllEmbeddingsForRerunAsync(cancellationToken);
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => inner.GetEmbeddingCoverageAsync(cancellationToken);
        public Task<int> ResetStalledEmbeddingClaimsAsync(CancellationToken cancellationToken = default) => inner.ResetStalledEmbeddingClaimsAsync(cancellationToken);
        public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.ReleaseEmbeddingClaimsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => inner.LoadAllEmbeddingsAsync(cancellationToken);
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => inner.PrewarmAsync(cancellationToken);
    }

    private sealed class GatedSearchClipStore(IClipStoreService inner) : IClipStoreService
    {
        public System.IObservable<System.Collections.Generic.IReadOnlyList<long>> ClipsRemoved => System.Reactive.Linq.Observable.Never<System.Collections.Generic.IReadOnlyList<long>>();

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

        public Task<int> ResetStalledOcrClaimsAsync(CancellationToken cancellationToken = default) => inner.ResetStalledOcrClaimsAsync(cancellationToken);
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkOcrForRerunAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllSucceededForRerunAsync(cancellationToken);
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => inner.GetOcrCoverageAsync(cancellationToken);
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => inner.ApplyMaintenanceAsync(cancellationToken);
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => inner.RebuildSensitivityMatchesAsync(cancellationToken);
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => inner.GetClipAtOffsetAsync(offset, cancellationToken);
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetByIdAsync(clipId, cancellationToken);
        public Task<byte[]?> GetSourceAppIconAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetSourceAppIconAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.GetByIdsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => inner.ClaimPendingEmbeddingsAsync(batchSize, cancellationToken);
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => inner.SaveEmbeddingBatchAsync(records, modelVersion, cancellationToken);
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetEmbeddingFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllEmbeddingsForRerunAsync(cancellationToken);
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => inner.GetEmbeddingCoverageAsync(cancellationToken);
        public Task<int> ResetStalledEmbeddingClaimsAsync(CancellationToken cancellationToken = default) => inner.ResetStalledEmbeddingClaimsAsync(cancellationToken);
        public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.ReleaseEmbeddingClaimsAsync(clipIds, cancellationToken);
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
    /// <summary>
    /// Fails searches while armed, so a test can make a refresh throw on
    /// demand and then observe whether the pipeline that requested it survived.
    /// </summary>
    private sealed class FlakySearchClipStore(IClipStoreService inner) : IClipStoreService
    {
        public System.IObservable<System.Collections.Generic.IReadOnlyList<long>> ClipsRemoved => System.Reactive.Linq.Observable.Never<System.Collections.Generic.IReadOnlyList<long>>();

        private volatile bool _shouldFail;
        private int _searchCount;
        private int _failedSearchCount;

        public int FailedSearchCount => Volatile.Read(ref _failedSearchCount);

        public int SearchCount => Volatile.Read(ref _searchCount);

        public void StartFailing() => _shouldFail = true;

        public void StopFailing() => _shouldFail = false;

        public Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _searchCount);
            if (_shouldFail)
            {
                Interlocked.Increment(ref _failedSearchCount);
                throw new InvalidOperationException("Simulated search failure.");
            }

            return inner.SearchAsync(filters, cancellationToken);
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

        public Task<int> ResetStalledOcrClaimsAsync(CancellationToken cancellationToken = default) => inner.ResetStalledOcrClaimsAsync(cancellationToken);
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkOcrForRerunAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllSucceededForRerunAsync(cancellationToken);
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => inner.GetOcrCoverageAsync(cancellationToken);
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => inner.ApplyMaintenanceAsync(cancellationToken);
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => inner.RebuildSensitivityMatchesAsync(cancellationToken);
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => inner.GetClipAtOffsetAsync(offset, cancellationToken);
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetByIdAsync(clipId, cancellationToken);
        public Task<byte[]?> GetSourceAppIconAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetSourceAppIconAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.GetByIdsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => inner.ClaimPendingEmbeddingsAsync(batchSize, cancellationToken);
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => inner.SaveEmbeddingBatchAsync(records, modelVersion, cancellationToken);
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetEmbeddingFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllEmbeddingsForRerunAsync(cancellationToken);
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => inner.GetEmbeddingCoverageAsync(cancellationToken);
        public Task<int> ResetStalledEmbeddingClaimsAsync(CancellationToken cancellationToken = default) => inner.ResetStalledEmbeddingClaimsAsync(cancellationToken);
        public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.ReleaseEmbeddingClaimsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => inner.LoadAllEmbeddingsAsync(cancellationToken);
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => inner.PrewarmAsync(cancellationToken);
    }

    private sealed class PagedSearchClipStore(IClipStoreService inner, int pageSize) : IClipStoreService
    {
        public System.IObservable<System.Collections.Generic.IReadOnlyList<long>> ClipsRemoved => System.Reactive.Linq.Observable.Never<System.Collections.Generic.IReadOnlyList<long>>();

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

        public Task<int> ResetStalledOcrClaimsAsync(CancellationToken cancellationToken = default) => inner.ResetStalledOcrClaimsAsync(cancellationToken);
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkOcrForRerunAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllSucceededForRerunAsync(cancellationToken);
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => inner.GetOcrCoverageAsync(cancellationToken);
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => inner.ApplyMaintenanceAsync(cancellationToken);
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => inner.RebuildSensitivityMatchesAsync(cancellationToken);
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => inner.GetClipAtOffsetAsync(offset, cancellationToken);
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetByIdAsync(clipId, cancellationToken);
        public Task<byte[]?> GetSourceAppIconAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetSourceAppIconAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.GetByIdsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => inner.ClaimPendingEmbeddingsAsync(batchSize, cancellationToken);
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => inner.SaveEmbeddingBatchAsync(records, modelVersion, cancellationToken);
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetEmbeddingFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllEmbeddingsForRerunAsync(cancellationToken);
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => inner.GetEmbeddingCoverageAsync(cancellationToken);
        public Task<int> ResetStalledEmbeddingClaimsAsync(CancellationToken cancellationToken = default) => inner.ResetStalledEmbeddingClaimsAsync(cancellationToken);
        public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.ReleaseEmbeddingClaimsAsync(clipIds, cancellationToken);
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
        public System.IObservable<System.Collections.Generic.IReadOnlyList<long>> ClipsRemoved => System.Reactive.Linq.Observable.Never<System.Collections.Generic.IReadOnlyList<long>>();

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

        public Task<int> ResetStalledOcrClaimsAsync(CancellationToken cancellationToken = default) => inner.ResetStalledOcrClaimsAsync(cancellationToken);
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => inner.MarkOcrForRerunAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllSucceededForRerunAsync(cancellationToken);
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => inner.GetOcrCoverageAsync(cancellationToken);
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => inner.ApplyMaintenanceAsync(cancellationToken);
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => inner.RebuildSensitivityMatchesAsync(cancellationToken);
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => inner.GetClipAtOffsetAsync(offset, cancellationToken);
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetByIdAsync(clipId, cancellationToken);
        public Task<byte[]?> GetSourceAppIconAsync(long clipId, CancellationToken cancellationToken = default) => inner.GetSourceAppIconAsync(clipId, cancellationToken);
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.GetByIdsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => inner.ClaimPendingEmbeddingsAsync(batchSize, cancellationToken);
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => inner.SaveEmbeddingBatchAsync(records, modelVersion, cancellationToken);
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => inner.SetEmbeddingFailureAsync(clipId, error, cancellationToken);
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => inner.MarkAllEmbeddingsForRerunAsync(cancellationToken);
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => inner.GetEmbeddingCoverageAsync(cancellationToken);
        public Task<int> ResetStalledEmbeddingClaimsAsync(CancellationToken cancellationToken = default) => inner.ResetStalledEmbeddingClaimsAsync(cancellationToken);
        public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => inner.ReleaseEmbeddingClaimsAsync(clipIds, cancellationToken);
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => inner.LoadAllEmbeddingsAsync(cancellationToken);
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => inner.PrewarmAsync(cancellationToken);
    }
    /// <summary>
    /// Regression test for B2d: captures were inserted at index 0
    /// unconditionally. Every ORDER BY clause leads with pinned-first, so a new
    /// (unpinned) clip was shown above the user's pinned clips until the next
    /// refresh quietly moved it back down.
    /// </summary>
    [AvaloniaFact]
    public async Task CapturedClip_IsInsertedBelowThePinnedClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        var pinned = await CaptureTextClipAsync(scope.ClipStoreService, "pinned clip");
        await CaptureTextClipAsync(scope.ClipStoreService, "ordinary clip");
        await scope.ClipStoreService.SetPinnedAsync(pinned.Id, true);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        await PumpAsync(() => viewModel.Clips.Count >= 2);

        Assert.Equal(pinned.Id, viewModel.Clips[0].Id);

        var fresh = await CaptureTextClipAsync(scope.ClipStoreService, "freshly captured");
        clipboardMonitor.Emit(fresh);
        await PumpAsync(() => viewModel.Clips.Any(clip => clip.Id == fresh.Id));

        Assert.Equal(pinned.Id, viewModel.Clips[0].Id);
        Assert.Equal(fresh.Id, viewModel.Clips[1].Id);
    }

    /// <summary>
    /// Regression test for B2d: the optimistic insert assumed the default sort.
    /// Under Oldest first a newly captured clip belongs at the bottom, so
    /// putting it on top contradicted the sort the user picked. We cannot work
    /// out the right position in memory, so the insert must be declined and an
    /// authoritative refresh requested instead.
    /// </summary>
    [AvaloniaFact]
    public async Task CapturedClip_UnderANonDefaultSort_IsNotPlacedOnTop()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        var oldest = await CaptureTextClipAsync(scope.ClipStoreService, "oldest clip");
        await CaptureTextClipAsync(scope.ClipStoreService, "middle clip");

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        viewModel.SelectedSortOption = viewModel.SortOptions.Single(option => option.Value == ClipSortOption.OldestFirst);
        await PumpAsync(() => viewModel.Clips.Count >= 2 && viewModel.Clips[0].Id == oldest.Id);

        var fresh = await CaptureTextClipAsync(scope.ClipStoreService, "freshly captured");
        clipboardMonitor.Emit(fresh);
        await PumpAsync(() => viewModel.Clips.Any(clip => clip.Id == fresh.Id));

        Assert.Equal(oldest.Id, viewModel.Clips[0].Id);
        Assert.Equal(fresh.Id, viewModel.Clips[^1].Id);
    }

    /// <summary>
    /// Regression test for B2d: the optimistic insert ignored the active
    /// filters, so a capture appeared in a list it does not belong to - here a
    /// list filtered to favourites only - and then vanished on the next
    /// refresh.
    /// </summary>
    [AvaloniaFact]
    public async Task CapturedClip_ThatFailsTheActiveFilter_IsNotShown()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        var favorite = await CaptureTextClipAsync(scope.ClipStoreService, "favourite clip");
        await CaptureTextClipAsync(scope.ClipStoreService, "ordinary clip");
        await scope.ClipStoreService.SetFavoriteAsync(favorite.Id, true);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        viewModel.ShowFavoritesOnly = true;
        await PumpAsync(() => viewModel.Clips.Count == 1 && viewModel.Clips[0].Id == favorite.Id);

        Assert.Equal(favorite.Id, Assert.Single(viewModel.Clips).Id);

        // Record every clip ever added to the list. Asserting on the final
        // contents would not detect the bug: the old code inserted the clip and
        // a later refresh removed it again, so only the flash in between is
        // observable, and racing it would make this test timing-dependent.
        var everAdded = new HashSet<long>();
        void OnClipsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            foreach (var item in e.NewItems?.OfType<ClipItemViewModel>() ?? Enumerable.Empty<ClipItemViewModel>())
            {
                everAdded.Add(item.Id);
            }
        }

        viewModel.Clips.CollectionChanged += OnClipsChanged;
        try
        {
            var fresh = await CaptureTextClipAsync(scope.ClipStoreService, "freshly captured, not a favourite");
            clipboardMonitor.Emit(fresh);

            // Long enough for the optimistic insert and for the deferred
            // refresh that replaces it to have run.
            await PumpAsync(() => false, maxAttempts: 12);

            Assert.DoesNotContain(fresh.Id, everAdded);
            Assert.DoesNotContain(viewModel.Clips, clip => clip.Id == fresh.Id);
            Assert.Equal(favorite.Id, Assert.Single(viewModel.Clips).Id);
        }
        finally
        {
            viewModel.Clips.CollectionChanged -= OnClipsChanged;
        }
    }

    /// <summary>
    /// Pinned clips order by when they were pinned, not by recency, so
    /// re-copying a pinned clip must not move it to the top of the pinned run.
    /// The optimistic path cannot work out the right position among the pinned
    /// clips, so it declines and lets a refresh decide.
    /// </summary>
    [AvaloniaFact]
    public async Task RecapturingAPinnedClip_DoesNotReorderThePinnedClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        var pinnedFirst = await CaptureTextClipAsync(scope.ClipStoreService, "pinned earlier");
        var pinnedSecond = await CaptureTextClipAsync(scope.ClipStoreService, "pinned later");

        // pinnedSecond is pinned last, so it sorts above pinnedFirst.
        await scope.ClipStoreService.SetPinnedAsync(pinnedFirst.Id, true);
        await Task.Delay(15);
        await scope.ClipStoreService.SetPinnedAsync(pinnedSecond.Id, true);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        await PumpAsync(() => viewModel.Clips.Count >= 2);

        Assert.Equal(pinnedSecond.Id, viewModel.Clips[0].Id);
        Assert.Equal(pinnedFirst.Id, viewModel.Clips[1].Id);

        // Snapshot the order after every mutation. Checking only the final
        // order would not detect the bug: an optimistic reorder is repaired by
        // the next refresh, so the pinned clips visibly swap and swap back.
        var snapshots = new List<string>();
        void OnClipsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => snapshots.Add(string.Join(",", viewModel.Clips.Select(clip => clip.Id)));

        viewModel.Clips.CollectionChanged += OnClipsChanged;
        try
        {
            // Re-copying the older-pinned clip bumps its recency but not its
            // pin time, so the order must not change.
            var recaptured = (await scope.ClipStoreService.GetByIdAsync(pinnedFirst.Id))!;
            clipboardMonitor.Emit(recaptured);
            await PumpAsync(() => false, maxAttempts: 12);

            var expected = $"{pinnedSecond.Id},{pinnedFirst.Id}";
            Assert.All(snapshots, snapshot => Assert.Equal(expected, snapshot));
            Assert.Equal(pinnedSecond.Id, viewModel.Clips[0].Id);
            Assert.Equal(pinnedFirst.Id, viewModel.Clips[1].Id);
        }
        finally
        {
            viewModel.Clips.CollectionChanged -= OnClipsChanged;
        }
    }

    private const string DeferredRefreshFailure = "Deferred refresh failed";

    /// <summary>
    /// The deferred-refresh stream is the only record that a declined capture
    /// is pending, so it must survive a failing refresh. OnError is terminal in
    /// Rx: without a Catch inside the SelectMany, one transient search failure
    /// would unsubscribe it for the rest of the session and every later
    /// declined capture would be dropped silently - the clip list would simply
    /// stop updating while a non-default sort was active.
    /// </summary>
    [AvaloniaFact]
    public async Task DeferredRefresh_SurvivesAFailingRefresh()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        var flakyStore = new FlakySearchClipStore(scope.ClipStoreService);
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService, clipStore: flakyStore);

        var traced = new ConcurrentQueue<string>();
        var listener = new TraceCaptureListener(traced);
        Trace.Listeners.Add(listener);
        try
        {
            await CaptureTextClipAsync(scope.ClipStoreService, "oldest clip");

            await viewModel.InitializeAsync();
            viewModel.SetMainWindowVisible(true);

            // Oldest first makes every capture take the declined path, which is
            // the one routed through the deferred-refresh stream.
            viewModel.SelectedSortOption = viewModel.SortOptions.Single(option => option.Value == ClipSortOption.OldestFirst);

            // Let the sort change's own throttled refresh finish before arming
            // the failure. Without this wait the failure lands on that refresh
            // instead, and the deferred stream is never exercised at all - an
            // earlier draft of this test failed exactly that way, passing
            // whether or not the code under test was correct.
            await PumpAsync(() => false, maxAttempts: 16);
            var searchesBeforeOutage = flakyStore.SearchCount;

            flakyStore.StartFailing();
            var duringOutage = await CaptureTextClipAsync(scope.ClipStoreService, "captured during the outage");
            clipboardMonitor.Emit(duringOutage);
            await PumpAsync(
                () => traced.Any(message => message.Contains(DeferredRefreshFailure, StringComparison.Ordinal)),
                maxAttempts: 30);

            Assert.True(
                flakyStore.SearchCount > searchesBeforeOutage,
                "The declined capture was expected to request a refresh.");

            // Without this the test would pass even if the failure had landed
            // on some other refresh - which is exactly how an earlier draft
            // fooled itself.
            Assert.Contains(traced, message => message.Contains(DeferredRefreshFailure, StringComparison.Ordinal));

            // Now that the deferred pipeline has seen a failure, a later
            // declined capture must still reach the list.
            flakyStore.StopFailing();
            var searchesAfterOutage = flakyStore.SearchCount;
            var afterRecovery = await CaptureTextClipAsync(scope.ClipStoreService, "captured after recovery");
            clipboardMonitor.Emit(afterRecovery);
            await PumpAsync(() => viewModel.Clips.Any(clip => clip.Id == afterRecovery.Id), maxAttempts: 30);

            Assert.True(
                flakyStore.SearchCount > searchesAfterOutage,
                "The deferred-refresh subscription died: the capture after recovery never triggered a search.");
            Assert.Contains(viewModel.Clips, clip => clip.Id == afterRecovery.Id);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    /// <summary>
    /// Collects Trace output so a test can assert which code path reported an
    /// error, not merely that some error was reported.
    /// </summary>
    private sealed class TraceCaptureListener(ConcurrentQueue<string> sink) : TraceListener
    {
        public override void Write(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }
    }
    /// <summary>
    /// Runs the dispatcher until <paramref name="isSatisfied"/> holds or the
    /// attempts run out. Returning without satisfying the condition is not an
    /// error here: callers assert the real expectation afterwards, and some
    /// callers deliberately wait for a fixed period instead.
    /// </summary>
    private static async Task PumpAsync(Func<bool> isSatisfied, int maxAttempts = 20)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (isSatisfied())
            {
                return;
            }

            await Task.Delay(50);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Delete has no canExecute gate and no guard of its own, and its targets
    /// come from GetCheckedOrSelectedClips, whose last fallback is
    /// Clips.FirstOrDefault(). That reads as "delete with nothing chosen
    /// destroys the newest clip", but it is not reachable: the SelectedClip
    /// setter coerces null back to Clips[0] while the list is non-empty, so
    /// there is always an explicit selection to act on. Both halves are pinned
    /// because that coercion looks like defensive noise and is the only thing
    /// standing between this command and a clip the user never pointed at.
    ///
    /// Captured through the real store rather than seeded into Clips, because
    /// the whole point is what survives in the database.
    /// </summary>
    [AvaloniaFact]
    public async Task DeleteWithNothingChecked_TakesOnlyTheSelectedClip()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        await CaptureTextClipAsync(scope.ClipStoreService, "oldest");
        await CaptureTextClipAsync(scope.ClipStoreService, "middle");
        var newest = await CaptureTextClipAsync(scope.ClipStoreService, "newest");

        await viewModel.RefreshCommand.Execute().ToTask();
        await PumpAsync(() => viewModel.Clips.Count == 3);
        Assert.Equal(3, viewModel.Clips.Count);

        // The state a user is in after a refresh drops their selection, or
        // before anything has been chosen at all.
        foreach (var clip in viewModel.Clips)
        {
            clip.IsChecked = false;
        }

        // The setter coerces this back to Clips[0]; that coercion is the thing
        // under test, so drive it rather than asserting the field.
        viewModel.SelectedClip = null;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(viewModel.SelectedClip);
        Assert.Same(viewModel.Clips[0], viewModel.SelectedClip);

        await viewModel.DeleteCheckedClipsCommand.Execute().ToTask();
        await PumpAsync(() => false, maxAttempts: 4);

        // Exactly the selected clip goes, and the two the user never pointed at
        // stay. Delete takes its targets from GetCheckedOrSelectedClips, so a
        // change that made "nothing checked" mean "everything" would land here.
        var remaining = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());
        Assert.Equal(2, remaining.TotalMatchingCount);
        Assert.DoesNotContain(remaining.Items, clip => clip.Id == newest.Id);

        await viewModel.DeleteCheckedClipsCommand.Execute().ToTask();
        await PumpAsync(() => false, maxAttempts: 4);

    }

    /// <summary>
    /// A copy that fails must not leave the capture gate armed.
    /// </summary>
    /// <remarks>
    /// The suppression gate is one-shot: arming it tells the monitor to skip the
    /// next clipboard change, because that change is the app writing the clip
    /// the user asked to copy. Arming it and then failing hands that skip to
    /// whatever the user copies next, which is silently missing from their
    /// history afterwards.
    ///
    /// Reachable without anything exotic: TryLoadImage returns null for an
    /// image it cannot decode and for any image over MaxClipSizeBytes, which
    /// defaults to 2 MB - smaller than plenty of real screenshots. The cost
    /// lands on the *following* copy, so it does not look related to the copy
    /// that actually failed, which is why it needed a test rather than a bug
    /// report.
    ///
    /// The successful case is asserted alongside deliberately. Without it a fix
    /// that simply never suppressed would pass, and then every copy the app
    /// makes would be captured straight back as a duplicate clip.
    /// </remarks>
    [AvaloniaFact]
    public async Task ACopyThatFails_DoesNotSwallowTheNextClipboardCapture()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();

        scope.SettingsService.SetCurrent(scope.SettingsService.Current with
        {
            MaxClipSizeBytes = AppSettings.MinMaxClipSizeBytes,
        });
        Assert.Equal(AppSettings.MinMaxClipSizeBytes, scope.SettingsService.Current.MaxClipSizeBytes);

        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        // Over the size limit rather than undecodable. TryLoadImage checks the
        // limit *before* decoding, which is what makes this reachable here at
        // all: the headless platform does not really decode, so garbage bytes
        // copy "successfully" and prove nothing. Two earlier setups asserted
        // nothing for that reason - one where the limit was silently clamped
        // back to the 2 MB default because Normalize rejects anything under
        // 256, and one that relied on a decode that never happened.
        viewModel.Clips.Add(new ClipItemViewModel(new ClipEntry
        {
            Id = 42,
            Content = "too large to copy",
            ContentBytes = new byte[AppSettings.MinMaxClipSizeBytes + 44],
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            SourceApp = "Tests",
            Hash = "hash-undecodable",
            LastCopiedAt = DateTimeOffset.UtcNow,
            FirstCopiedAt = DateTimeOffset.UtcNow,
        }));
        viewModel.SelectedClip = viewModel.Clips[0];
        Dispatcher.UIThread.RunJobs();

        Assert.False(await viewModel.TryCopySelectedForPasteAsync());
        Assert.Equal(0, clipboardMonitor.PendingSuppressions);

        // Control: a copy that succeeds must still arm exactly one, or the app
        // re-captures everything it writes as a duplicate clip.
        //
        // An image, not text. With a text control this test passed against a
        // mutant that removed the suppression from the image write entirely -
        // the only difference that mattered was the branch, and the control
        // exercised the other one.
        viewModel.Clips.Clear();
        viewModel.Clips.Add(new ClipItemViewModel(CreateImageClipEntry(
            CreatePngBytes(unchecked((int)0xFF00FF00)),
            "small enough to copy")));
        viewModel.SelectedClip = viewModel.Clips[0];
        Dispatcher.UIThread.RunJobs();

        Assert.True(await viewModel.TryCopySelectedForPasteAsync());
        Assert.Equal(1, clipboardMonitor.PendingSuppressions);
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

    // The selected-image pane used to decode through a value converter, which runs inline on
    // the UI thread. Arrowing through a list of screenshots therefore paid a full PNG decode
    // per keystroke, on the thread drawing the frame.
    [AvaloniaFact]
    public async Task SelectedImagePreview_DecodesOffTheUiThread_AtFullResolution()
    {
        var bytes = CreateLargePngBytes(1200, 900);
        using var item = new ClipItemViewModel(new ClipEntry
        {
            Id = 11,
            Content = "screenshot",
            ContentBytes = bytes,
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            SourceApp = "Tests",
            Hash = Guid.NewGuid().ToString("N"),
            ByteSize = bytes.LongLength,
            ImageWidth = 1200,
            ImageHeight = 900,
        });

        Assert.Null(item.FullImage);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (item.FullImage is null && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();

        var full = item.FullImage;
        Assert.NotNull(full);
        Assert.NotEqual(ThumbnailDecodeWidthForTests, full!.PixelSize.Width);
        Assert.NotSame(item.PreviewThumbnailImage, full);
    }

    // The view model property above is only worth anything if the pane actually binds to it;
    // a binding left on the old byte-array converter would decode inline again and no
    // view-model test would notice.
    [AvaloniaFact]
    public async Task SelectedImagePane_BindsToTheBackgroundDecodedImage()
    {
        using var harness = MainWindowTestHarness.Create();
        var bytes = CreateLargePngBytes(1200, 900);
        var item = new ClipItemViewModel(new ClipEntry
        {
            Id = 12,
            Content = "screenshot",
            ContentBytes = bytes,
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            SourceApp = "Tests",
            Hash = Guid.NewGuid().ToString("N"),
            ByteSize = bytes.LongLength,
            ImageWidth = 1200,
            ImageHeight = 900,
        });

        harness.ViewModel.Clips.Add(item);
        harness.ViewModel.SelectedClip = item;
        Dispatcher.UIThread.RunJobs();

        var preview = harness.Window.FindControl<Image>("SelectedImagePreview");
        Assert.NotNull(preview);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (preview!.Source is null && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(preview.Source);
        Assert.Same(item.FullImage, preview.Source);
    }

    // A decoded bitmap costs width x height x 4 bytes however small it is drawn, so a
    // screenshot-sized clip used to hold tens of megabytes just to fill an 84x48 row.
    // The headless drawing backend does not really decode, so these assertions read the
    // size the load asked the decoder for rather than real pixels - which is exactly the
    // routing decision under test, and they hold either way: a real decode of the wide
    // fixture also lands on 256, and a real decode of the narrow one stays at 64.
    [AvaloniaTheory]
    [InlineData(1200, 900, 256)]
    [InlineData(64, 48, null)]
    public async Task RowThumbnail_DecodesWideImagesDown_ButNeverUpscalesNarrowOnes(int width, int height, int? expectedDecodeWidth)
    {
        var bytes = CreateLargePngBytes(width, height);
        using var item = new ClipItemViewModel(new ClipEntry
        {
            Id = 7,
            Content = "screenshot",
            ContentBytes = bytes,
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            SourceApp = "Tests",
            Hash = Guid.NewGuid().ToString("N"),
            ByteSize = bytes.LongLength,
            ImageWidth = width,
            ImageHeight = height,
        });

        Assert.Null(item.PreviewThumbnailImage);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (item.PreviewThumbnailImage is null && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();

        var thumbnail = item.PreviewThumbnailImage;
        Assert.NotNull(thumbnail);

        if (expectedDecodeWidth is { } expected)
        {
            Assert.Equal(expected, thumbnail!.PixelSize.Width);
        }
        else
        {
            Assert.True(
                thumbnail!.PixelSize.Width < ThumbnailDecodeWidthForTests,
                $"A {width}px source was decoded at {thumbnail.PixelSize.Width}px, so it was upscaled to the thumbnail width.");
        }
    }

    private const int ThumbnailDecodeWidthForTests = 256;

    /// <summary>
    /// A real, decodable grayscale PNG of the requested size. The 1x1 fixture above cannot
    /// say anything about decode scaling, and the encoder here is deliberately dependency
    /// free so the test does not rely on a rendering backend being available to produce its
    /// own input.
    /// </summary>
    private static byte[] CreateLargePngBytes(int width, int height)
    {
        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        var crcTable = BuildCrcTable();

        uint Crc(byte[] data)
        {
            var c = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFFu;
        }

        void WriteChunk(Stream output, string type, byte[] payload)
        {
            var length = BitConverter.GetBytes(payload.Length);
            Array.Reverse(length);
            output.Write(length);

            var typed = new byte[4 + payload.Length];
            Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
            payload.CopyTo(typed, 4);
            output.Write(typed);

            var crc = BitConverter.GetBytes(Crc(typed));
            Array.Reverse(crc);
            output.Write(crc);
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        var w = BitConverter.GetBytes(width);
        var h = BitConverter.GetBytes(height);
        Array.Reverse(w);
        Array.Reverse(h);
        w.CopyTo(header, 0);
        h.CopyTo(header, 4);
        header[8] = 8;  // bit depth
        header[9] = 0;  // colour type: grayscale
        WriteChunk(png, "IHDR", header);

        // One filter byte plus one sample per pixel, per scanline.
        var raw = new byte[height * (1 + width)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width);
            raw[row] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                raw[row + 1 + x] = (byte)((x + y) & 0xFF);
            }
        }

        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(png, "IDAT", deflated.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] CreatePngBytes(int bgraColor)
    {
        _ = bgraColor;
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");
    }
}