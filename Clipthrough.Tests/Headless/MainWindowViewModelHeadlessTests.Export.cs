using System;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Localization;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>Export and tool copies of sensitive clips (F-18, OD-3a).</summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    private static async Task<(TemporaryDatabaseScope Scope, Clipthrough.ViewModels.MainWindowViewModel ViewModel)> SensitiveClipSelectedAsync()
    {
        var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clip = await CaptureTextClipAsync(scope.ClipStoreService, "password = NOT-A-REAL-CREDENTIAL");
        await scope.ClipStoreService.SetSensitiveAsync(clip.Id, true);
        var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService());
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        for (var i = 0; i < 200 && viewModel.SelectedClip is null; i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(viewModel.SelectedClip?.Clip.IsSensitive);
        return (scope, viewModel);
    }

    [AvaloniaFact]
    public async Task ExportingASensitiveClip_AsksFirst_AndCancellingWritesNothing()
    {
        var (scope, viewModel) = await SensitiveClipSelectedAsync();
        using (scope)
        using (viewModel)
        {
            string? askedTitle = null;
            viewModel.ConfirmAsync = (title, _, _, _) => { askedTitle = title; return Task.FromResult(false); };

            await viewModel.ExportSelectedCommand.Execute().ToTask();

            Assert.Equal(AppText.ExportSensitiveConfirmTitle, askedTitle);
            Assert.Equal(0, scope.ClipExportService.ExportCount);
            Assert.Equal(AppText.ExportSensitiveCancelledStatus, viewModel.StatusText);
        }
    }

    [AvaloniaFact]
    public async Task ExportingASensitiveClip_GoesAheadWhenConfirmed()
    {
        var (scope, viewModel) = await SensitiveClipSelectedAsync();
        using (scope)
        using (viewModel)
        {
            viewModel.ConfirmAsync = (_, _, _, _) => Task.FromResult(true);

            await viewModel.ExportSelectedCommand.Execute().ToTask();

            Assert.Equal(1, scope.ClipExportService.ExportCount);
        }
    }

    [AvaloniaFact]
    public async Task ExportingASensitiveClip_WithNoWayToAsk_WritesNothing()
    {
        var (scope, viewModel) = await SensitiveClipSelectedAsync();
        using (scope)
        using (viewModel)
        {
            await viewModel.ExportSelectedCommand.Execute().ToTask();

            Assert.Equal(0, scope.ClipExportService.ExportCount);
        }
    }

    [AvaloniaFact]
    public async Task OpenInEditor_UsesASessionToolCopy_NotAnExport()
    {
        var (scope, viewModel) = await SensitiveClipSelectedAsync();
        using (scope)
        using (viewModel)
        {
            await viewModel.OpenInEditorCommand.Execute().ToTask();

            Assert.Equal(1, scope.ClipExportService.ToolExportCount);
            Assert.Equal(0, scope.ClipExportService.ExportCount);
        }
    }

    [AvaloniaFact]
    public async Task Compare_UsesSessionToolCopies_NotExports()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await CaptureTextClipAsync(scope.ClipStoreService, "left side");
        await CaptureTextClipAsync(scope.ClipStoreService, "right side");
        await scope.SettingsService.SaveAsync(AppSettings.Default with { ExternalDiffToolPath = Environment.ProcessPath! });
        using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService());
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        for (var i = 0; i < 200 && viewModel.Clips.Count < 2; i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        viewModel.Clips[0].IsChecked = true;
        viewModel.Clips[1].IsChecked = true;

        await viewModel.CompareClipsCommand.Execute().ToTask();

        Assert.Equal(2, scope.ClipExportService.ToolExportCount);
        Assert.Equal(0, scope.ClipExportService.ExportCount);
    }
}