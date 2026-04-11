using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Database;
using Clipthrough.Localization;
using Clipthrough.Models;
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
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, sessionLogService);

        await viewModel.InitializeAsync();

        var firstClip = await CaptureTextClipAsync(scope.ClipStoreService, "first");
        clipboardMonitor.Emit(firstClip);
        Dispatcher.UIThread.RunJobs();

        var secondClip = await CaptureTextClipAsync(scope.ClipStoreService, "second");
        clipboardMonitor.Emit(secondClip);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(secondClip.Id, viewModel.SelectedClip?.Id);
    }

    [AvaloniaFact]
    public async Task SelectAllAndFavoriteSelected_UpdateAllCheckedClips()
    {
        using var scope = new TemporaryDatabaseScope();
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
    public async Task SessionLogs_FilterByLevelAndSearch()
    {
        using var scope = new TemporaryDatabaseScope();
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

    private static MainWindowViewModel CreateViewModel(
        TemporaryDatabaseScope scope,
        TestClipboardMonitorService clipboardMonitor,
        TestSystemInteractionService systemInteraction,
        TestSessionLogService sessionLogService)
    {
        return new MainWindowViewModel(
            scope.ClipStoreService,
            clipboardMonitor,
            new TestClipSampleDataService(),
            scope.SettingsService,
            systemInteraction,
            scope.StorageOptionsService,
            scope.NotificationService,
            sessionLogService,
            scope.DatabaseInitializer);
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
}
