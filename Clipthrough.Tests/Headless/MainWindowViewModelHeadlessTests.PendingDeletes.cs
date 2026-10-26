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
/// Soft delete and its five-second undo window, off the happy path.
/// </summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    private static async Task<MainWindowViewModel> CreateLoadedViewModelAsync(
        TemporaryDatabaseScope scope,
        TestClipboardMonitorService monitor,
        int expectedClips)
    {
        var viewModel = CreateViewModel(scope, monitor, new TestSystemInteractionService(), new TestSessionLogService());
        await viewModel.InitializeAsync();
        for (var attempt = 0; attempt < 40 && viewModel.Clips.Count < expectedClips; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(expectedClips, viewModel.Clips.Count);
        return viewModel;
    }

    /// <summary>
    /// Copying a deleted clip's text again inside the undo window is the user
    /// wanting it back. Capture dedupes onto the row that is pending deletion,
    /// so before the fix the commit then deleted the fresh copy from the list
    /// and the database, with no undo left (F-13, confirmed live).
    /// </summary>
    [AvaloniaFact]
    public async Task RecopyDuringUndoWindow_KeepsTheFreshCopy()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await CaptureTextClipAsync(scope.ClipStoreService, "keep me");
        await CaptureTextClipAsync(scope.ClipStoreService, "deploy token xyz");

        var monitor = new TestClipboardMonitorService();
        using var viewModel = await CreateLoadedViewModelAsync(scope, monitor, expectedClips: 2);
        viewModel.SetMainWindowVisible(true);

        var target = viewModel.Clips.Single(c => c.Clip.Content == "deploy token xyz");
        var id = target.Id;
        viewModel.SelectedClip = target;
        var deleting = viewModel.DeleteSelectedCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(viewModel.Clips, c => c.Id == id);

        var recaptured = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "deploy token xyz",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("deploy token xyz"),
        });
        Assert.Equal(id, recaptured!.Id); // the fixture: dedupe reuses the pending row
        monitor.Emit(recaptured);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(viewModel.Clips, c => c.Id == id);

        await deleting; // the undo window elapses and the commit runs
        for (var i = 0; i < 10; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }

        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(id));
        Assert.Contains(viewModel.Clips, c => c.Id == id);
    }

    /// <summary>
    /// Withdrawing a re-captured id must not withdraw the rest of its batch:
    /// the other clips deleted alongside it still go.
    /// </summary>
    [AvaloniaFact]
    public async Task RecopyDuringUndoWindow_OtherClipsInTheBatchAreStillDeleted()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var gone = await CaptureTextClipAsync(scope.ClipStoreService, "delete me too");
        await CaptureTextClipAsync(scope.ClipStoreService, "deploy token xyz");

        var monitor = new TestClipboardMonitorService();
        using var viewModel = await CreateLoadedViewModelAsync(scope, monitor, expectedClips: 2);
        viewModel.SetMainWindowVisible(true);

        await viewModel.SelectAllClipsCommand.Execute().ToTask();
        var deleting = viewModel.DeleteCheckedClipsCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        var recaptured = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "deploy token xyz",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("deploy token xyz"),
        });
        monitor.Emit(recaptured!);
        Dispatcher.UIThread.RunJobs();

        await deleting;
        for (var i = 0; i < 10; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }

        Assert.Null(await scope.ClipStoreService.GetByIdAsync(gone.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(recaptured!.Id));
    }
}
