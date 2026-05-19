using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

// IStorageProvider / IStorageItem aren't user-implementable on Avalonia 12,
// so tests here cover the drop-IN parsing path (which only needs a
// DataTransfer with built-in formats and inline bytes) and skip the drag-OUT
// payload path (which requires real Avalonia storage and is exercised by
// manual verification per the plan).
public class DragDropServiceTests
{
    private readonly DragDropService _service = new();

    [Fact]
    public async Task TryBuildCaptureRequests_TextOnly_ProducesPlainTextRequest()
    {
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText("hello world");
        transfer.Add(item);

        var requests = await _service.TryBuildCaptureRequestsAsync(transfer, sourceInfo: null);

        var request = Assert.Single(requests);
        Assert.Equal(ContentType.Text, request.ContentType);
        Assert.Equal(ClipContentFormat.PlainText, request.ContentFormat);
        Assert.Equal("hello world", request.ContentText);
        Assert.Equal(ClipImportKinds.DragDrop, request.ImportKind);
    }

    [Fact]
    public async Task TryBuildCaptureRequests_HtmlAndText_PrefersHtmlAsRichText()
    {
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText("plain text fallback");
        item.Set(DataFormat.CreateStringPlatformFormat("HTML Format"), "<p>hello <b>world</b></p>");
        transfer.Add(item);

        var requests = await _service.TryBuildCaptureRequestsAsync(transfer, sourceInfo: null);

        var request = Assert.Single(requests);
        Assert.Equal(ContentType.RichText, request.ContentType);
        Assert.Equal(ClipContentFormat.Html, request.ContentFormat);
        Assert.Equal("plain text fallback", request.ContentText);
        Assert.Contains("<p>hello <b>world</b></p>", Encoding.UTF8.GetString(request.ContentBytes));
        Assert.Equal(ClipImportKinds.DragDrop, request.ImportKind);
    }

    [Fact]
    public async Task TryBuildCaptureRequests_RtfWithoutHtml_ProducesRtfRichTextRequest()
    {
        var rtfBody = "{\\rtf1\\ansi hello}";
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText("hello");
        item.Set(DataFormat.CreateStringPlatformFormat("Rich Text Format"), rtfBody);
        transfer.Add(item);

        var requests = await _service.TryBuildCaptureRequestsAsync(transfer, sourceInfo: null);

        var request = Assert.Single(requests);
        Assert.Equal(ContentType.RichText, request.ContentType);
        Assert.Equal(ClipContentFormat.Rtf, request.ContentFormat);
        Assert.Equal(rtfBody, Encoding.UTF8.GetString(request.ContentBytes));
        Assert.Equal(ClipImportKinds.DragDrop, request.ImportKind);
    }

    [Fact]
    public async Task TryBuildCaptureRequests_EmptyTransfer_ReturnsEmptyList()
    {
        var requests = await _service.TryBuildCaptureRequestsAsync(new DataTransfer(), sourceInfo: null);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task TryBuildCaptureRequests_StampsSourceInfo()
    {
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText("text");
        transfer.Add(item);

        var sourceInfo = new ClipboardSourceApplicationInfo("Explorer", @"C:\Windows\explorer.exe", null, "File Explorer");
        var requests = await _service.TryBuildCaptureRequestsAsync(transfer, sourceInfo);

        var request = Assert.Single(requests);
        Assert.Equal("Explorer", request.SourceApp);
        Assert.Equal(@"C:\Windows\explorer.exe", request.SourceAppPath);
        Assert.Equal("File Explorer", request.SourceWindowTitle);
    }
}
