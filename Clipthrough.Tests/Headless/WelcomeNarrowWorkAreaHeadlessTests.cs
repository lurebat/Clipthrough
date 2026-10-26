using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The window's 1100 DIP minimum width is what the clip workspace needs, but it
/// applied to the first-run welcome screen too. On any work area narrower than
/// that - 1920x1080 at 200%, 1366x768 at 125%, a portrait monitor - even a
/// maximized window was laid out at 1100 DIP and clipped, and "Create library",
/// right-aligned in the footer, was drawn off-screen. A mouse user could not
/// finish setup. (audit F-49; seen live on a 1440 px display at 250% = 576 DIP)
/// </summary>
/// <remarks>
/// Asserted against the work-area width rather than the window's client size:
/// with the minimum still in force the client size itself grows to 1100, so a
/// button "inside the client area" proves nothing.
/// </remarks>
public sealed class WelcomeNarrowWorkAreaHeadlessTests
{
    private const double WorkAreaWidth = 576;
    private const double WorkAreaHeight = 1200;

    [AvaloniaFact]
    public async Task OnANarrowWorkArea_CreateLibraryIsOnScreenAndTabReachable()
    {
        using var harness = MainWindowTestHarness.Create();
        await OpenWelcomeAsync(harness);

        harness.Window.Width = WorkAreaWidth;
        harness.Window.Height = WorkAreaHeight;
        Dispatcher.UIThread.RunJobs();

        var create = CreateLibraryButton(harness);
        var topLeft = create.TranslatePoint(new Point(0, 0), harness.Window)!.Value;
        var right = topLeft.X + create.Bounds.Width;
        var bottom = topLeft.Y + create.Bounds.Height;
        Assert.True(
            topLeft.X >= 0 && right <= WorkAreaWidth && topLeft.Y >= 0 && bottom <= WorkAreaHeight,
            $"Create library is drawn at x={topLeft.X:F0}..{right:F0}, y={topLeft.Y:F0}..{bottom:F0}; the work area is {WorkAreaWidth}x{WorkAreaHeight}");

        Assert.True(
            harness.Window.Bounds.Width <= WorkAreaWidth,
            $"the window is {harness.Window.Bounds.Width} DIP wide on a {WorkAreaWidth} DIP work area");

        // The inputs keep a usable width and scroll sideways rather than being
        // squeezed into what is left beside the fixed left column.
        var path = harness.Window.FindControl<TextBox>("WelcomeDatabasePathTextBox")!;
        Assert.True(path.Bounds.Width >= 200, $"the database path box was squeezed to {path.Bounds.Width:F0} DIP");

        var lastCheckBox = harness.Window.GetVisualDescendants().OfType<CheckBox>().Last(box => box.IsEffectivelyVisible);
        lastCheckBox.Focus();
        Dispatcher.UIThread.RunJobs();
        harness.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();

        var focused = TopLevel.GetTopLevel(harness.Window)!.FocusManager!.GetFocusedElement();
        Assert.Same(create, focused);
    }

    [AvaloniaFact]
    public async Task AtTheDefaultSize_TheWelcomeContentDoesNotScrollSideways()
    {
        using var harness = MainWindowTestHarness.Create();
        await OpenWelcomeAsync(harness);

        var scroller = harness.Window.FindControl<TextBox>("WelcomeDatabasePathTextBox")!
            .GetVisualAncestors().OfType<ScrollViewer>().First();

        Assert.True(
            scroller.Extent.Width <= scroller.Viewport.Width,
            $"the welcome content is {scroller.Extent.Width:F0} DIP wide in a {scroller.Viewport.Width:F0} DIP viewport at the default window size");
    }

    [AvaloniaFact]
    public void TheWorkspaceKeepsItsMinimumWidth()
    {
        using var harness = MainWindowTestHarness.Create();

        Assert.True(harness.ViewModel.IsMainWorkspaceVisible, "the workspace is not showing, so its minimum is not under test");
        Assert.Equal(1100, harness.Window.MinWidth);
    }

    private static async Task OpenWelcomeAsync(MainWindowTestHarness harness)
    {
        await harness.ViewModel.InitializeAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(harness.ViewModel.IsWelcomeOpen, "the welcome screen did not open, so this is not the first-run layout");
    }

    private static Button CreateLibraryButton(MainWindowTestHarness harness)
        => harness.Window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.IsEffectivelyVisible && ReferenceEquals(button.Command, harness.ViewModel.SaveSettingsCommand));
}
