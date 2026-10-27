using System;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// U-D5 part 2 (F-10) through the window's own commands. A list row carries only
/// the first <see cref="ClipEntry.ListContentLength"/> characters of its text, so
/// every command that acts on a clip has to hydrate it first or it hands the user
/// part of their clip. Each test selects a long clip as it arrives from the list
/// read and checks the command used all of it.
/// </summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    private static readonly string LongClipText =
        string.Concat(Enumerable.Range(0, 900).Select(static i => $"line {i:D4} of a long log\n"));

    private static async Task<(MainWindowViewModel ViewModel, ClipItemViewModel Row)> OpenOnALongListRowAsync(
        TemporaryDatabaseScope scope,
        TestClipboardMonitorService clipboardMonitor,
        TestSystemInteractionService systemInteraction)
    {
        await PrepareInitializedScopeAsync(scope);
        var captured = await CaptureTextClipAsync(scope.ClipStoreService, LongClipText);
        // A newer clip takes the automatic selection, whose hydration would
        // otherwise fill the long row in before the test looks at it.
        await CaptureTextClipAsync(scope.ClipStoreService, "newest");
        var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, new TestSessionLogService());
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        for (var attempt = 0; attempt < 80 && viewModel.Clips.All(c => c.Id != captured.Id); attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        var row = viewModel.Clips.Single(c => c.Id == captured.Id);
        Assert.True(row.Clip.IsContentTruncated, "Setup: the clip must arrive from the list read as a truncated row.");
        return (viewModel, row);
    }

    [AvaloniaFact]
    public async Task SelectingALongListRow_ShowsThePrefixFirst_ThenAllOfIt_AndOnlyThenAllowsEditing()
    {
        using var scope = new TemporaryDatabaseScope();
        var (viewModel, row) = await OpenOnALongListRowAsync(scope, new TestClipboardMonitorService(), new TestSystemInteractionService());
        using var _ = viewModel;

        viewModel.SelectedClip = row;
        Assert.False(viewModel.IsSelectedClipTextEditable, "A row holding a prefix must not be editable: the edit would be saved as a clip missing everything after it.");

        await row.EnsureContentHydratedAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(row.Clip.IsContentTruncated);
        Assert.Equal(LongClipText, viewModel.SelectedClipRawContent);
        Assert.True(viewModel.IsSelectedClipTextEditable);
    }

    [AvaloniaFact]
    public async Task CopyingALongListRow_CopiesAllOfIt()
    {
        using var scope = new TemporaryDatabaseScope();
        var systemInteraction = new TestSystemInteractionService();
        var (viewModel, row) = await OpenOnALongListRowAsync(scope, new TestClipboardMonitorService(), systemInteraction);
        using var _ = viewModel;

        viewModel.SelectedClip = row;
        await viewModel.CopySelectedCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(LongClipText, systemInteraction.LastCopiedText);
    }

    [AvaloniaFact]
    public async Task CopyingALongListRowAsPlainText_CopiesAllOfIt()
    {
        using var scope = new TemporaryDatabaseScope();
        var systemInteraction = new TestSystemInteractionService();
        var (viewModel, row) = await OpenOnALongListRowAsync(scope, new TestClipboardMonitorService(), systemInteraction);
        using var _ = viewModel;

        viewModel.SelectedClip = row;
        await viewModel.CopySelectedAsPlainTextCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(LongClipText, systemInteraction.LastCopiedText);
    }

    [AvaloniaFact]
    public async Task TransformingALongListRow_TransformsAllOfIt()
    {
        using var scope = new TemporaryDatabaseScope();
        var systemInteraction = new TestSystemInteractionService();
        var (viewModel, row) = await OpenOnALongListRowAsync(scope, new TestClipboardMonitorService(), systemInteraction);
        using var _ = viewModel;

        row.IsChecked = true;
        await viewModel.ApplyTextTransformationCommand.Execute(TextTransformation.UpperCase).ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(LongClipText.ToUpperInvariant(), systemInteraction.LastCopiedText);
    }

    [AvaloniaFact]
    public async Task ExportingALongListRow_ExportsAllOfIt()
    {
        using var scope = new TemporaryDatabaseScope();
        var (viewModel, row) = await OpenOnALongListRowAsync(scope, new TestClipboardMonitorService(), new TestSystemInteractionService());
        using var _ = viewModel;

        viewModel.SelectedClip = row;
        await viewModel.ExportSelectedCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(LongClipText, scope.ClipExportService.LastExportedContent);
    }
}
