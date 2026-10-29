using Avalonia;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The geometry that keeps windows on screen. Sizes are DIP, positions pixels;
/// every case that matters here is one where the two differ.
/// </summary>
public sealed class WindowGeometryTests
{
    private static readonly Size DefaultClient = new(1360, 860);
    private static readonly Size NoMinimum = new(0, 0);
    private static readonly Size NoFrame = new(0, 0);

    // The requester's display: 1440x2992 at 250%, taskbar at the bottom.
    private static readonly ScreenArea Portrait = new(new PixelRect(0, 0, 1440, 2872), 2.5);

    // 1280x720 at 150%: 853x432 DIP of working area.
    private static readonly ScreenArea Laptop = new(new PixelRect(0, 0, 1280, 648), 1.5);

    private static readonly ScreenArea FullHd = new(new PixelRect(0, 0, 1920, 1032), 1.0);

    [Fact]
    public void OnThePortraitDisplay_TheWindowShrinksAndMovesInsideTheWorkingArea()
    {
        // The frame at (244,244) px is where Windows placed it in the live
        // reproduction of audit F-49.
        var frameExtra = new Size(6.4, 6.4);

        var fit = WindowGeometry.Fit(DefaultClient, NoMinimum, frameExtra, new PixelPoint(244, 244), Portrait);

        Assert.NotNull(fit);
        var (size, position) = fit.Value;
        Assert.True(size.Width <= 576 - frameExtra.Width, $"client width {size.Width} DIP does not fit 576 DIP");
        Assert.Equal(DefaultClient.Height, size.Height);
        AssertFrameInside(size, frameExtra, position, Portrait);
    }

    [Fact]
    public void ATooShortWorkingArea_ShrinksTheHeight()
    {
        var fit = WindowGeometry.Fit(DefaultClient, NoMinimum, new Size(16, 39), new PixelPoint(0, 0), Laptop);

        Assert.NotNull(fit);
        Assert.True(fit.Value.ClientSize.Height <= (648 / 1.5) - 39, $"client height {fit.Value.ClientSize.Height} DIP");
        Assert.True(fit.Value.ClientSize.Width <= (1280 / 1.5) - 16, $"client width {fit.Value.ClientSize.Width} DIP");
    }

    [Fact]
    public void AWindowThatFits_IsLeftAlone()
    {
        Assert.Null(WindowGeometry.Fit(DefaultClient, NoMinimum, new Size(16, 39), new PixelPoint(100, 0), FullHd));
    }

    [Fact]
    public void AWindowThatFitsButHangsOffTheEdge_IsMovedNotResized()
    {
        var fit = WindowGeometry.Fit(DefaultClient, NoMinimum, new Size(16, 39), new PixelPoint(1000, 100), FullHd);

        Assert.NotNull(fit);
        Assert.Equal(DefaultClient, fit.Value.ClientSize);
        Assert.Equal(1920 - (1360 + 16), fit.Value.Position.X);
        Assert.Equal(100, fit.Value.Position.Y);
    }

    [Fact]
    public void ASecondaryScreen_IsRespected()
    {
        // A monitor to the left of the primary has a negative origin.
        var left = new ScreenArea(new PixelRect(-1440, 0, 1440, 2872), 2.5);

        var fit = WindowGeometry.Fit(DefaultClient, NoMinimum, new Size(6.4, 6.4), new PixelPoint(-1200, 244), left);

        Assert.NotNull(fit);
        Assert.Equal(-1440, fit.Value.Position.X);
    }

    [Fact]
    public void TheMinimumWins_AndTheCaptionStaysOnScreen()
    {
        // A minimum larger than the screen cannot be fitted; the top-left edge
        // is pinned so the caption can still be grabbed.
        var fit = WindowGeometry.Fit(DefaultClient, new Size(700, 500), new Size(16, 39), new PixelPoint(300, 300), Laptop);

        Assert.NotNull(fit);
        Assert.Equal((1280 / 1.5) - 16, fit.Value.ClientSize.Width, 3);
        Assert.Equal(500, fit.Value.ClientSize.Height);
        Assert.Equal(new PixelPoint(0, 0), fit.Value.Position);
    }

    [Fact]
    public void NearTheCaret_TheFrameIsSizedWithTheTargetScreensScaling()
    {
        // The old placement treated a 1360 DIP window as 1360 px. On the 250%
        // portrait screen that put x at 80 px and the right edge 1390 px past the
        // screen. 576 DIP of client is 1440 px: the frame must start at 0.
        var position = WindowGeometry.PlaceNearCaret(new PixelPoint(100, 500), new Size(576, 860), NoFrame, Portrait);

        Assert.Equal(0, position.X);
        Assert.Equal(520, position.Y);
    }

    [Fact]
    public void NearTheCaret_ACaretOnAnotherMonitorUsesThatMonitorsScaling()
    {
        // The window lives on a 100% screen; the caret is on a 250% screen to its
        // right. 400 DIP wide is 1000 px there, not 400.
        var right = new ScreenArea(new PixelRect(1920, 0, 1440, 2872), 2.5);

        var position = WindowGeometry.PlaceNearCaret(new PixelPoint(3000, 100), new Size(400, 300), NoFrame, right);

        Assert.Equal(1920 + 1440 - 1000, position.X);
        Assert.Equal(120, position.Y);
    }

    [Fact]
    public void NearTheCaret_NoRoomBelow_FlipsAbove()
    {
        var position = WindowGeometry.PlaceNearCaret(new PixelPoint(100, 600), new Size(400, 300), NoFrame, Laptop);

        Assert.Equal(100, position.X);
        Assert.Equal(600 - 450 - 10, position.Y);
    }

    [Fact]
    public void NearTheCaret_TallerThanTheScreen_PinsToTheTop()
    {
        var position = WindowGeometry.PlaceNearCaret(new PixelPoint(100, 300), new Size(400, 600), NoFrame, Laptop);

        Assert.Equal(0, position.Y);
    }

    [Fact]
    public void ARememberedWindowOnAMonitorThatIsGone_ComesBackOnThePrimary()
    {
        var remembered = new WindowPlacement(new Size(1200, 800), new PixelPoint(-2400, 100));

        var restored = WindowGeometry.Restore(remembered, NoMinimum, new Size(16, 39), [FullHd]);

        Assert.Equal(new Size(1200, 800), restored.ClientSize);
        AssertFrameInside(restored.ClientSize, new Size(16, 39), restored.Position, FullHd);
    }

    [Fact]
    public void ARememberedWindowLargerThanTheScreen_IsClamped()
    {
        var remembered = new WindowPlacement(new Size(2400, 1300), new PixelPoint(0, 0));

        var restored = WindowGeometry.Restore(remembered, new Size(480, 360), NoFrame, [Laptop]);

        Assert.True(restored.ClientSize.Width <= 1280 / 1.5 + 0.01, $"width {restored.ClientSize.Width}");
        Assert.True(restored.ClientSize.Height <= 648 / 1.5 + 0.01, $"height {restored.ClientSize.Height}");
        AssertFrameInside(restored.ClientSize, NoFrame, restored.Position, Laptop);
    }

    [Fact]
    public void ARememberedWindowGoesBackToTheScreenItWasOn()
    {
        var right = new ScreenArea(new PixelRect(1920, 0, 2560, 1392), 1.0);
        var remembered = new WindowPlacement(new Size(1200, 800), new PixelPoint(2100, 150));

        var restored = WindowGeometry.Restore(remembered, NoMinimum, NoFrame, [FullHd, right]);

        Assert.Equal(remembered, restored);
    }

    private static void AssertFrameInside(Size client, Size frameExtra, PixelPoint position, ScreenArea screen)
    {
        var right = position.X + (int)System.Math.Ceiling((client.Width + frameExtra.Width) * screen.Scaling);
        var bottom = position.Y + (int)System.Math.Ceiling((client.Height + frameExtra.Height) * screen.Scaling);
        var area = screen.WorkingArea;
        Assert.True(
            position.X >= area.X && right <= area.Right && position.Y >= area.Y && bottom <= area.Bottom,
            $"the frame spans ({position.X},{position.Y})..({right},{bottom}) px; the working area is {area}");
    }
}