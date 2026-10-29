using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The selection a restart lands on (F-50). List rows carry no payload
/// (lazy hydration, S16), so a rich clip shows its markup only once the
/// selected clip is hydrated. Selecting a clip by hand did that; the clip
/// selected automatically at startup did not, and the preview showed one
/// flattened paragraph until the user moved off and back.
/// </summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    private const string RichMarkup = "<p>Word audit heading</p><p>Body with <b>bold</b> text.</p>";

    [AvaloniaFact]
    public async Task AfterARestart_TheAutoSelectedRichClipPreviewsItsMarkup()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await CaptureTextClipAsync(scope.ClipStoreService, "older plain clip");
        var rich = await CaptureRichTextClipAsync(scope.ClipStoreService, RichMarkup, ClipContentFormat.Html);

        // A fresh view model over an existing library is what a restart is.
        using var viewModel = await CreateLoadedViewModelAsync(scope, new TestClipboardMonitorService(), expectedClips: 2);
        viewModel.SetMainWindowVisible(true);

        Assert.Equal(rich.Id, viewModel.SelectedClip?.Id);
        Assert.True(
            await WaitUntilAsync(
                () => (viewModel.EditedClipText ?? string.Empty).Contains("<b>bold</b>", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)),
            $"The preview got '{viewModel.EditedClipText}' instead of the clip's markup.");
        Assert.Equal(ClipContentFormat.Html, viewModel.SelectedClipContentFormat);
    }
}
