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
        // The selection re-presents the clip a dispatcher turn after hydration
        // replaces it, and under load that turn can come after a single RunJobs.
        for (var i = 0; i < 80 && viewModel.SelectedClipRawContent != LongClipText; i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.False(row.Clip.IsContentTruncated);
        Assert.Equal(LongClipText, viewModel.SelectedClipRawContent);
        Assert.True(viewModel.IsSelectedClipTextEditable);
    }

    /// <summary>
    /// Hydration makes the row whole a dispatcher turn before the selection
    /// re-presents it, and editability used to follow the row: in that gap the
    /// editor was editable while it still held the 4096-character prefix. An edit
    /// made there and committed by moving the selection was captured as a new clip
    /// holding the prefix and the edit - everything after the prefix gone, the
    /// loss the read-only prefix exists to prevent. Driven from the row's
    /// FullImage notification, which hydration raises right after it replaces the row's\n    /// clip and before the selection's re-presentation runs.
    /// </summary>
    [AvaloniaFact]
    public async Task BetweenHydrationAndThePreview_TheEditorStaysReadOnly()
    {
        using var scope = new TemporaryDatabaseScope();
        var (viewModel, row) = await OpenOnALongListRowAsync(scope, new TestClipboardMonitorService(), new TestSystemInteractionService());
        using var _ = viewModel;
        var other = viewModel.Clips.First(c => c.Id != row.Id);

        bool? editableInTheGap = null;
        int? editorLengthInTheGap = null;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ClipItemViewModel.FullImage) || editableInTheGap is not null || row.Clip.IsContentTruncated)
            {
                return;
            }

            editableInTheGap = viewModel.IsSelectedClipTextEditable;
            editorLengthInTheGap = viewModel.EditedClipText.Length;
            if (viewModel.IsSelectedClipTextEditable)
            {
                viewModel.EditedClipText += " edited";
                viewModel.SelectedClip = other;
            }
        };

        viewModel.SelectedClip = row;
        await row.EnsureContentHydratedAsync();
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.NotNull(editableInTheGap);
        Assert.True(editorLengthInTheGap < LongClipText.Length, "Setup: the gap must be observed while the editor still holds the prefix.");
        Assert.False(editableInTheGap, "The editor was editable while it held only the prefix.");
        var everything = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { Limit = 50 });
        Assert.DoesNotContain(everything.Items, c => c.ContentPrefix.StartsWith("line 0000", StringComparison.Ordinal) && c.Id != row.Id);
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

    /// <summary>
    /// Selects the long row and arranges for the selection to move to another
    /// clip - marked sensitive in the store - from inside the row's hydration,
    /// i.e. after a command has started awaiting it and before it resumes.
    /// </summary>
    private static async Task<(MainWindowViewModel ViewModel, ClipItemViewModel Row, ClipItemViewModel Other)> SelectionMovesDuringHydrationAsync(
        TemporaryDatabaseScope scope)
    {
        await PrepareInitializedScopeAsync(scope);
        var captured = await CaptureTextClipAsync(scope.ClipStoreService, LongClipText);
        // Marked before the window loads: a later change would make the next
        // refresh replace this row's view model with a new one.
        var secret = await CaptureTextClipAsync(scope.ClipStoreService, "password = NOT-A-REAL-CREDENTIAL");
        await scope.ClipStoreService.SetSensitiveAsync(secret.Id, true);
        var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), new TestSystemInteractionService(), new TestSessionLogService());
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        for (var attempt = 0; attempt < 80 && viewModel.Clips.Count < 2; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        var row = viewModel.Clips.Single(c => c.Id == captured.Id);
        var other = viewModel.Clips.Single(c => c.Id == secret.Id);
        Assert.True(other.Clip.IsSensitive, "Setup: the clip the selection moves to must be sensitive.");

        var moved = false;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClipItemViewModel.FullImage) && !moved && !row.Clip.IsContentTruncated)
            {
                moved = true;
                viewModel.SelectedClip = other;
            }
        };

        viewModel.SelectedClip = row;
        Assert.True(row.Clip.IsContentTruncated, "Setup: the row must still need hydration when the command starts.");
        return (viewModel, row, other);
    }

    /// <summary>
    /// Rubber-duck F-07-1. Export hydrates the clip before writing it, and the
    /// selection can move while that runs. The export must still write the clip
    /// Export was clicked on - a non-sensitive one, so nobody was asked - and
    /// must not write the newly selected sensitive clip, which nobody confirmed.
    /// </summary>
    [AvaloniaFact]
    public async Task ExportingALongListRow_ExportsThatRow_EvenIfTheSelectionMovesDuringHydration()
    {
        using var scope = new TemporaryDatabaseScope();
        var (viewModel, row, other) = await SelectionMovesDuringHydrationAsync(scope);
        using var _ = viewModel;
        var asked = 0;
        viewModel.ConfirmAsync = (_, _, _, _) => { asked++; return Task.FromResult(true); };

        await viewModel.ExportSelectedCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(other, viewModel.SelectedClip);
        Assert.Equal(0, asked);
        Assert.Equal(1, scope.ClipExportService.ExportCount);
        Assert.EndsWith($"clip-export-{row.Id}", scope.ClipExportService.LastExportDirectory, StringComparison.Ordinal);
        Assert.Equal(LongClipText, scope.ClipExportService.LastExportedContent);
    }

    /// <summary>
    /// Rubber-duck F-07-1, Open in editor. The same hydration await: the editor
    /// must get a copy of the clip the command was run on, not whatever became
    /// selected while it was being loaded.
    /// </summary>
    [AvaloniaFact]
    public async Task OpeningALongListRowInTheEditor_OpensThatRow_EvenIfTheSelectionMovesDuringHydration()
    {
        using var scope = new TemporaryDatabaseScope();
        var (viewModel, row, other) = await SelectionMovesDuringHydrationAsync(scope);
        using var _ = viewModel;

        await viewModel.OpenInEditorCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(other, viewModel.SelectedClip);
        Assert.Equal(1, scope.ClipExportService.ToolExportCount);
        Assert.EndsWith($"clip-tool-copy-{row.Id}", scope.ClipExportService.LastExportDirectory, StringComparison.Ordinal);
    }
}
