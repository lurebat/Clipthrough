using Clipthrough.Services.Platform;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The tray icon is registered as <c>NOTIFYICON_VERSION_4</c>, which packs the
/// icon's <c>uID</c> into the high word of <c>lParam</c> and the notification
/// event into the low word. The callback compared the whole value, so no
/// comparison could ever match and the first-run "Clipthrough moved to the
/// tray" toast invited a click that did nothing - at the moment a new user has
/// just lost the window and is being told where it went.
/// (round 2, bugs-opus F6)
/// </summary>
/// <remarks>
/// The window procedure itself cannot be driven from a test: it is Windows-only
/// P/Invoke reached through a real message pump. The decision it makes is pure,
/// so that is what is extracted and tested here. The packed values below are
/// what Windows actually delivers for <c>uID = 1</c>, which is what
/// <c>CreateNotificationData</c> sets.
/// </remarks>
public sealed class TrayActivationNotificationTests
{
    private const int IconId = 1;

    private static nint Packed(uint notification) => unchecked((nint)((IconId << 16) | notification));

    [Theory]
    [InlineData(0x0400u)] // NIN_SELECT - a left click under version 4
    [InlineData(0x0401u)] // NIN_KEYSELECT - the same activation from the keyboard
    [InlineData(0x0405u)] // NIN_BALLOONUSERCLICK - the toast itself
    public void APackedActivation_IsRecognised(uint notification)
    {
        Assert.True(
            SystemInteractionService.IsTrayActivationNotification(Packed(notification)),
            $"0x{notification:X4} packed with the icon id was not recognised, so the click does nothing");
    }

    /// <summary>
    /// The bug in its original form: the same events unpacked were recognised,
    /// which is why reading the code suggested it worked.
    /// </summary>
    [Theory]
    [InlineData(0x0400u)]
    [InlineData(0x0405u)]
    [InlineData(0x0202u)] // WM_LBUTTONUP - the legacy layout, if NIM_SETVERSION failed
    public void AnUnpackedActivation_IsStillRecognised(uint notification)
    {
        Assert.True(SystemInteractionService.IsTrayActivationNotification(unchecked((nint)notification)));
    }

    /// <summary>
    /// The control. Masking must not turn every callback into an activation:
    /// the icon receives mouse-move and right-click notifications constantly,
    /// and treating those as a click would raise the window under the cursor.
    /// </summary>
    [Theory]
    [InlineData(0x0200u)] // WM_MOUSEMOVE
    [InlineData(0x0204u)] // WM_RBUTTONDOWN
    [InlineData(0x0205u)] // WM_RBUTTONUP
    [InlineData(0x0402u)] // NIN_BALLOONSHOW
    [InlineData(0x0403u)] // NIN_BALLOONHIDE
    [InlineData(0x0404u)] // NIN_BALLOONTIMEOUT - the toast expired unclicked
    public void ANonActivationCallback_IsIgnored(uint notification)
    {
        Assert.False(
            SystemInteractionService.IsTrayActivationNotification(Packed(notification)),
            $"0x{notification:X4} was treated as a click, which would raise the window unbidden");
    }

    /// <summary>
    /// The high word must not be able to manufacture a match on its own, or a
    /// different icon id would change which callbacks count.
    /// </summary>
    [Fact]
    public void TheIconIdAloneIsNotAnActivation()
    {
        Assert.False(SystemInteractionService.IsTrayActivationNotification(unchecked((nint)(0x0405 << 16))));
    }
}
