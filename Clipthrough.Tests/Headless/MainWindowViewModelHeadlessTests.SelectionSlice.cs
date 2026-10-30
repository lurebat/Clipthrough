using System;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>Transforming the selected slice of a clip (rubber-duck F-12-1).</summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    /// <summary>
    /// HTML -> Markdown awaits VellumText's async importer, so the user can move
    /// the editor selection while it runs. The result must be spliced at the
    /// range that was converted, not at wherever the selection is when the
    /// conversion finishes.
    /// </summary>
    [AvaloniaFact]
    public async Task SelectionSliceTransform_SplicesAtTheRangeItConverted_WhenTheSelectionMovesMeanwhile()
    {
        const string slice = "<b>bold</b>";
        const string tail = " then plain tail";
        const string content = slice + tail;

        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        using var viewModel = CreateViewModel(scope, clipboardMonitor, systemInteraction, new TestSessionLogService());
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);

        var clip = await CaptureTextClipAsync(scope.ClipStoreService, content);
        clipboardMonitor.Emit(clip);
        for (var i = 0; i < 200 && (viewModel.SelectedClip?.Id != clip.Id || viewModel.EditedClipText != content); i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(content, viewModel.EditedClipText);
        viewModel.EditedClipSelectionStart = 0;
        viewModel.EditedClipSelectionLength = slice.Length;

        var running = viewModel.ApplyTextTransformationCommand.Execute(TextTransformation.HtmlToMarkdown).ToTask();

        // The conversion has to be in flight here, or moving the selection
        // happens before the command reads it and the test proves nothing.
        Assert.False(running.IsCompleted, "HTML -> Markdown completed synchronously; the selection cannot move during it");
        viewModel.EditedClipSelectionStart = slice.Length + 1;
        viewModel.EditedClipSelectionLength = 4;

        await running;
        Dispatcher.UIThread.RunJobs();

        var convertedSlice = await TextTransformationService.ApplyAsync(TextTransformation.HtmlToMarkdown, slice);
        Assert.NotEqual(slice, convertedSlice);
        Assert.DoesNotContain("<b>", convertedSlice, StringComparison.Ordinal);
        Assert.Equal(convertedSlice + tail, systemInteraction.LastCopiedText);
    }
}
