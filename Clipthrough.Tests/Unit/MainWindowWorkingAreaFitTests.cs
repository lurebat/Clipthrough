using Avalonia;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The main window opens at a default 1360x860 DIP. On a smaller working area
/// Windows clamps the window, but the first layout follows the requested size,
/// so on a 1440 px display at 250% (576 DIP) the welcome screen opened laid out
/// wider than the window and "Create library" was off-screen until the user
/// resized or maximized. The window is now fitted on first open. (audit F-49)
/// </summary>
public sealed class MainWindowWorkingAreaFitTests
{
    private static readonly Size DefaultClient = new(1360, 860);

    [Fact]
    public void OnTheAuditDisplay_TheWindowShrinksAndMovesInsideTheWorkingArea()
    {
        // 1440x2872 px working area at 250%, the window's frame at (244,244) px
        // as Windows placed it in the live reproduction.
        var workingArea = new PixelRect(0, 0, 1440, 2872);
        var frameExtra = new Size(6.4, 6.4);

        var fit = MainWindow.FitToWorkingArea(DefaultClient, frameExtra, new PixelPoint(244, 244), workingArea, 2.5);

        Assert.NotNull(fit);
        var (size, position) = fit.Value;
        Assert.True(size.Width <= 576 - frameExtra.Width, $"client width {size.Width} DIP does not fit 576 DIP");
        Assert.Equal(DefaultClient.Height, size.Height);
        var outerRight = position.X + (int)System.Math.Ceiling((size.Width + frameExtra.Width) * 2.5);
        Assert.True(position.X >= workingArea.X && outerRight <= workingArea.Right, $"the frame spans x={position.X}..{outerRight} px on a {workingArea.Width} px working area");
    }

    [Fact]
    public void ATooShortWorkingArea_ShrinksTheHeight()
    {
        // 1366x768 at 125%: 614 DIP of height before the taskbar.
        var workingArea = new PixelRect(0, 0, 1366, 728);

        var fit = MainWindow.FitToWorkingArea(DefaultClient, new Size(16, 39), new PixelPoint(0, 0), workingArea, 1.25);

        Assert.NotNull(fit);
        Assert.True(fit.Value.Size.Height <= (728 / 1.25) - 39);
        Assert.True(fit.Value.Size.Width <= (1366 / 1.25) - 16);
    }

    [Fact]
    public void AWindowThatFits_IsLeftAlone()
    {
        var fit = MainWindow.FitToWorkingArea(DefaultClient, new Size(16, 39), new PixelPoint(100, 100), new PixelRect(0, 0, 1920, 1040), 1.0);

        Assert.Null(fit);
    }

    [Fact]
    public void AWindowThatFitsButHangsOffTheEdge_IsMovedNotResized()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1040);

        var fit = MainWindow.FitToWorkingArea(DefaultClient, new Size(16, 39), new PixelPoint(1000, 100), workingArea, 1.0);

        Assert.NotNull(fit);
        Assert.Equal(DefaultClient, fit.Value.Size);
        Assert.Equal(1920 - (1360 + 16), fit.Value.Position.X);
        Assert.Equal(100, fit.Value.Position.Y);
    }

    [Fact]
    public void ASecondaryScreen_IsRespected()
    {
        // A monitor to the left of the primary has a negative origin.
        var workingArea = new PixelRect(-1440, 0, 1440, 2872);

        var fit = MainWindow.FitToWorkingArea(DefaultClient, new Size(6.4, 6.4), new PixelPoint(-1200, 244), workingArea, 2.5);

        Assert.NotNull(fit);
        Assert.Equal(-1440, fit.Value.Position.X);
    }
}
