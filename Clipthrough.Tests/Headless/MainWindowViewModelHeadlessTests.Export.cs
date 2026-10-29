using System;
using System.Linq;
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

    /// <summary>
    /// Rubber-duck F-06-1. The confirmation is for the clip that was selected
    /// when Export was clicked. A capture can move the selection while the
    /// dialog is open; the export must still write that clip, and must not
    /// write the newly selected one, which nobody confirmed.
    /// </summary>
    [AvaloniaFact]
    public async Task ExportingASensitiveClip_ExportsTheConfirmedClip_EvenIfTheSelectionMovesWhileAsking()
    {
        var (scope, viewModel) = await SensitiveClipSelectedAsync();
        using (scope)
        using (viewModel)
        {
            var confirmed = viewModel.SelectedClip!;
            var other = await CaptureTextClipAsync(scope.ClipStoreService, "another secret NOT-A-REAL-CREDENTIAL-2");
            await scope.ClipStoreService.SetSensitiveAsync(other.Id, true);
            await viewModel.RefreshCommand.Execute().ToTask();
            for (var i = 0; i < 200 && viewModel.Clips.Count < 2; i++)
            {
                await Task.Delay(25);
                Dispatcher.UIThread.RunJobs();
            }

            var otherItem = Assert.Single(viewModel.Clips, c => c.Clip.Id == other.Id);
            viewModel.SelectedClip = viewModel.Clips.Single(c => c.Clip.Id == confirmed.Clip.Id);

            var answer = new TaskCompletionSource<bool>();
            var asked = 0;
            viewModel.ConfirmAsync = (_, _, _, _) => { asked++; return answer.Task; };

            var export = viewModel.ExportSelectedCommand.Execute().ToTask();
            Assert.Equal(1, asked);

            viewModel.SelectedClip = otherItem;
            answer.SetResult(true);
            await export;

            Assert.Equal(1, asked);
            Assert.Equal(1, scope.ClipExportService.ExportCount);
            Assert.EndsWith($"clip-export-{confirmed.Clip.Id}", scope.ClipExportService.LastExportDirectory, StringComparison.Ordinal);
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