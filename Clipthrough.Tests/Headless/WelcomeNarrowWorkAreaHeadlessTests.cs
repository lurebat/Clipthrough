using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Tests.Layout;
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

        // The inputs keep a usable width: the form stacks above the explanation
        // rather than being squeezed into what is left beside it.
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

    /// <summary>
    /// The window used to keep the workspace's 1100x640 DIP minimum, which
    /// no longer exists: the workspace reflows down to 480x360 instead
    /// (ResponsiveWorkspaceHeadlessTests). The minimum is what the smallest
    /// supported screen, 1280x720 at 150%, can hold with room to spare.
    /// </summary>
    [AvaloniaFact]
    public void TheWindowMinimumFitsTheSmallestSupportedScreen()
    {
        using var harness = MainWindowTestHarness.Create();

        Assert.True(harness.ViewModel.IsMainWorkspaceVisible, "the workspace is not showing, so its minimum is not under test");
        Assert.Equal(480, harness.Window.MinWidth);
        Assert.Equal(360, harness.Window.MinHeight);
    }

    /// <summary>
    /// The first fix for F-49 lowered only the minimum width. The 640 DIP
    /// minimum height still held the window taller than a 1280x720 laptop at
    /// 150% (about 400 DIP of client area once maximized), so Create library,
    /// in the welcome footer, was drawn below the screen.
    /// </summary>
    [AvaloniaFact]
    public async Task OnAShortWorkArea_CreateLibraryIsOnScreen()
    {
        using var harness = MainWindowTestHarness.Create();
        await OpenWelcomeAsync(harness);

        harness.Window.Width = 853;
        harness.Window.Height = 401;
        Dispatcher.UIThread.RunJobs();

        OnScreen.AssertVisible("Create library", CreateLibraryButton(harness), harness.Window, new Size(853, 401));
    }

    /// <summary>The same short screen, where Unlock was below the screen too.</summary>
    [AvaloniaFact]
    public void OnAShortWorkArea_UnlockIsReachable()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.ViewModel.OpenPasswordPromptForTests();
        Dispatcher.UIThread.RunJobs();

        harness.Window.Width = 853;
        harness.Window.Height = 401;
        Dispatcher.UIThread.RunJobs();

        var unlock = harness.Window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, harness.ViewModel.UnlockDatabaseCommand));
        OnScreen.AssertReachable("Unlock", unlock, harness.Window, new Size(853, 401));
    }

    [AvaloniaFact]
    public async Task OnAPortraitScreen_TheFormStacksInsteadOfScrollingSideways()
    {
        using var harness = MainWindowTestHarness.Create();
        await OpenWelcomeAsync(harness);

        harness.Window.Width = WorkAreaWidth;
        harness.Window.Height = WorkAreaHeight;
        Dispatcher.UIThread.RunJobs();

        var path = harness.Window.FindControl<TextBox>("WelcomeDatabasePathTextBox")!;
        var scroller = path.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.True(
            scroller.Extent.Width <= scroller.Viewport.Width + 0.5,
            $"the welcome content is {scroller.Extent.Width:F0} DIP wide in a {scroller.Viewport.Width:F0} DIP viewport");
        OnScreen.AssertVisible("database path", path, harness.Window, new Size(WorkAreaWidth, WorkAreaHeight));
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
