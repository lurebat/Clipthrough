using System.Text;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The monitor's half of F-52: after a paste, Remote Desktop's rdpclip
/// re-publishes the clipboard and raises a second change notification,
/// attributed to "SxS RDP Clipboard Monitor". The first notification is
/// suppressed before reading; this is the decision taken on the second one,
/// once its content has been read.
/// </summary>
public sealed class ClipboardRepublishTests
{
    private static ClipCaptureRequest Text(string text, string source) => new()
    {
        ContentType = ContentType.Text,
        ContentFormat = ClipContentFormat.PlainText,
        ContentText = text,
        ContentBytes = Encoding.UTF8.GetBytes(text),
        SourceApp = source,
    };

    [Fact]
    public void TheRdpRepublishOfAPaste_IsRecognisedAsOurOwn()
    {
        using var monitor = new ClipboardMonitorService(null!, null!, null!, new TestSettingsService());

        monitor.SuppressNext("beta");

        Assert.True(monitor.IsOwnRepublish(Text("beta", "SxS RDP Clipboard Monitor")));
    }

    [Fact]
    public void ACopyOfDifferentTextRightAfterAPaste_IsStillCaptured()
    {
        using var monitor = new ClipboardMonitorService(null!, null!, null!, new TestSettingsService());

        monitor.SuppressNext("beta");

        Assert.False(monitor.IsOwnRepublish(Text("what the user copied", "Notepad")));
    }

    [Fact]
    public void AnImage_IsNeverMatchedByItsWindowTitle()
    {
        using var monitor = new ClipboardMonitorService(null!, null!, null!, new TestSettingsService());
        monitor.SuppressNext("Q3 layoffs - Outlook");

        var image = new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentText = "Q3 layoffs - Outlook",
            ContentBytes = [1, 2, 3],
        };

        Assert.False(monitor.IsOwnRepublish(image));
    }
}
