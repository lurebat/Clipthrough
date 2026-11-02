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
        await HeadlessStartup.InitializeAndWaitForDatabaseAsync(viewModel);

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

    /// <summary>
    /// Each delete owns its own undo window. Delete a clip, copy it again (which
    /// withdraws that delete), then delete it again: the first delete's timer
    /// used to commit the second delete when it fired, because commit matched
    /// pending entries by id alone. The clip was destroyed three seconds into
    /// its new undo window and the second Undo found nothing to restore.
    /// </summary>
    /// <remarks>
    /// The two seconds between the deletes is the second window's margin: the
    /// assertions run when the first timer has fired and the second still has
    /// about two seconds left.
    /// </remarks>
    [AvaloniaFact]
    public async Task DeleteRecopyDeleteAgain_FirstTimerDoesNotCommitTheSecondDelete()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await CaptureTextClipAsync(scope.ClipStoreService, "keep me");
        await CaptureTextClipAsync(scope.ClipStoreService, "deploy token xyz");

        var monitor = new TestClipboardMonitorService();
        using var viewModel = await CreateLoadedViewModelAsync(scope, monitor, expectedClips: 2);
        viewModel.SetMainWindowVisible(true);

        var id = viewModel.Clips.Single(c => c.Clip.Content == "deploy token xyz").Id;
        viewModel.SelectedClip = viewModel.Clips.Single(c => c.Id == id);
        var firstDelete = viewModel.DeleteSelectedCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(viewModel.Clips, c => c.Id == id);

        var recaptured = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "deploy token xyz",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("deploy token xyz"),
        });
        Assert.Equal(id, recaptured!.Id);
        monitor.Emit(recaptured);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(viewModel.Clips, c => c.Id == id);

        await Task.Delay(TimeSpan.FromSeconds(2));
        Dispatcher.UIThread.RunJobs();
        viewModel.SelectedClip = viewModel.Clips.Single(c => c.Id == id);
        var secondDelete = viewModel.DeleteSelectedCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(viewModel.Clips, c => c.Id == id);
        var secondUndo = scope.NotificationService.LastNotification!.Actions.Single();

        await firstDelete; // the first undo window elapses
        Dispatcher.UIThread.RunJobs();
        Assert.False(secondDelete.IsCompleted, "The second undo window closed before the assertions; the margin was lost.");
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(id));

        await secondUndo.ExecuteAsync();
        await secondDelete;
        for (var i = 0; i < 10; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }

        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(id));
        Assert.Contains(viewModel.Clips, c => c.Id == id);
    }

    /// <summary>
    /// The user was told "Clip deleted". Quitting inside the undo window used
    /// to cancel the delete, so the clip - a password, say - was back on the
    /// next launch and in every backup after (F-14). Pending deletes now
    /// commit on exit (OD-8a, S41).
    /// </summary>
    [AvaloniaFact]
    public async Task DeleteThenQuitWithinUndoWindow_DeletesTheClip()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await CaptureTextClipAsync(scope.ClipStoreService, "keep me");
        await CaptureTextClipAsync(scope.ClipStoreService, "my secret password 123");

        var viewModel = await CreateLoadedViewModelAsync(scope, new TestClipboardMonitorService(), expectedClips: 2);
        var target = viewModel.Clips.Single(c => c.Clip.Content == "my secret password 123");
        var keep = viewModel.Clips.Single(c => c.Clip.Content == "keep me").Id;
        viewModel.SelectedClip = target;
        var deleting = viewModel.DeleteSelectedCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(viewModel.Clips, c => c.Id == target.Id);

        // MainWindow.OnClosed disposes the view model.
        viewModel.Dispose();
        await deleting;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(await scope.ClipStoreService.GetByIdAsync(target.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(keep));
    }

    /// <summary>
    /// Undo still wins: a delete the user took back before quitting stays
    /// taken back.
    /// </summary>
    [AvaloniaFact]
    public async Task DeleteUndoThenQuit_KeepsTheClip()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await CaptureTextClipAsync(scope.ClipStoreService, "changed my mind");

        var viewModel = await CreateLoadedViewModelAsync(scope, new TestClipboardMonitorService(), expectedClips: 1);
        var target = viewModel.Clips[0];
        viewModel.SelectedClip = target;
        var deleting = viewModel.DeleteSelectedCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        var undo = scope.NotificationService.LastNotification!.Actions.Single();
        await undo.ExecuteAsync();
        await deleting;
        Dispatcher.UIThread.RunJobs();

        viewModel.Dispose();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(target.Id));
    }
}
