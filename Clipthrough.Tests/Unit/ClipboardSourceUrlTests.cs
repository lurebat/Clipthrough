using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Live check C7 (2026-10-29): a link copied from Edge was stored with a
/// source_url of CJK-looking text, and "Open source URL" logged
/// <c>Unsupported URL scheme: 楦敬⼺⼯㩃唯敳獲…</c>. That text is the page's
/// UTF-8 URL read through a UTF-16 accessor: each pair of bytes became one
/// character (楦 is 0x6966, "fi"), followed by whatever lay past the URL.
/// </summary>
/// <remarks>
/// Windows hands a clipboard format over as one buffer, and Avalonia offers it
/// two ways: as bytes, and as a string decoded as UTF-16. Each fixture below
/// sets both views of the same buffer, which is what the real transport
/// returns, so the test exercises the choice the reader makes between them.
/// The buffer is the URL, its terminating NUL and some bytes past it: Windows
/// rounds a clipboard allocation up, and the live string carried trailing
/// characters after the URL.
/// </remarks>
public sealed class ClipboardSourceUrlTests
{
    private const string Url = "file:///C:/Users/tester/live/c3.html";

    /// <summary>The first eight characters the live capture stored.</summary>
    private const string LiveMojibakePrefix = "楦敬⼺⼯㩃唯敳獲";

    private static byte[] Buffer(byte[] payload, byte[] terminator)
        => [.. payload, .. terminator, 0xA7, 0x3C, 0x11];

    /// <summary>
    /// One clipboard buffer offered both ways, as the Windows transport does: as
    /// the raw bytes, or as those bytes decoded as UTF-16. An in-memory
    /// DataTransferItem holds one value per format name, so it cannot model this.
    /// </summary>
    private sealed class TwoViewTransfer(string format, byte[] buffer) : IAsyncDataTransfer, IAsyncDataTransferItem
    {
        private readonly DataFormat<byte[]> _bytes = DataFormat.CreateBytesPlatformFormat(format);
        private readonly DataFormat<string> _string = DataFormat.CreateStringPlatformFormat(format);

        public IReadOnlyList<DataFormat> Formats => [_bytes, _string];

        public IReadOnlyList<IAsyncDataTransferItem> Items => [this];

        public Task<object?> TryGetRawAsync(DataFormat dataFormat)
        {
            if (dataFormat is DataFormat<byte[]> && string.Equals(dataFormat.Identifier, format, StringComparison.Ordinal))
            {
                return Task.FromResult<object?>(buffer);
            }

            if (dataFormat is DataFormat<string> && string.Equals(dataFormat.Identifier, format, StringComparison.Ordinal))
            {
                var even = buffer.Length % 2 == 0 ? buffer : [.. buffer, 0];
                return Task.FromResult<object?>(Encoding.Unicode.GetString(even));
            }

            return Task.FromResult<object?>(null);
        }

        public void Dispose()
        {
        }
    }

    private static IAsyncDataTransfer WithBothViews(string format, byte[] buffer) => new TwoViewTransfer(format, buffer);

    [Fact]
    public void TheFixture_ReproducesTheLiveCapture()
    {
        var buffer = Buffer(Encoding.UTF8.GetBytes(Url), [0]);
        Assert.StartsWith(LiveMojibakePrefix, Encoding.Unicode.GetString(buffer[..16]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChromiumsSourceUrl_IsReadAsUtf8_UpToItsTerminator()
    {
        var transfer = WithBothViews("Chromium internal source URL", Buffer(Encoding.UTF8.GetBytes(Url), [0]));

        Assert.Equal(Url, await ClipboardMonitorService.ReadSourceUrlAsync(transfer));
    }

    [Fact]
    public async Task ChromiumsSourceUrl_KeepsNonAsciiCharacters()
    {
        const string url = "https://example.com/caf\u00e9/\u65e5\u672c";
        var transfer = WithBothViews("Chromium internal source URL", Buffer(Encoding.UTF8.GetBytes(url), [0]));

        Assert.Equal(url, await ClipboardMonitorService.ReadSourceUrlAsync(transfer));
    }

    [Fact]
    public async Task TheWideUrlFormat_IsReadAsUtf16_UpToItsTerminator()
    {
        var transfer = WithBothViews("UniformResourceLocatorW", Buffer(Encoding.Unicode.GetBytes(Url), [0, 0]));

        Assert.Equal(Url, await ClipboardMonitorService.ReadSourceUrlAsync(transfer));
    }

    [Fact]
    public async Task TheAnsiUrlFormat_IsReadByteForByte_UpToItsTerminator()
    {
        var transfer = WithBothViews("UniformResourceLocator", Buffer(Encoding.ASCII.GetBytes(Url), [0]));

        Assert.Equal(Url, await ClipboardMonitorService.ReadSourceUrlAsync(transfer));
    }

    /// <summary>
    /// The ANSI format is in the system's ANSI code page, not Latin-1. The two
    /// agree on ASCII, so the fixture above cannot tell them apart; these bytes
    /// differ in every candidate. 0x80 is the euro sign in cp1252 and a C1
    /// control in Latin-1; 0xC0 is Cyrillic A in cp1251 and A-grave in both
    /// cp1252 and Latin-1.
    /// </summary>
    [Theory]
    [InlineData(1252, (byte)0x80, "https://example.com/price/\u20ac")]
    [InlineData(1251, (byte)0xC0, "https://example.com/price/\u0410")]
    public async Task TheAnsiUrlFormat_IsDecodedInTheAnsiCodePage(int codePage, byte nonAscii, string expected)
    {
        byte[] payload = [.. Encoding.ASCII.GetBytes("https://example.com/price/"), nonAscii];
        var transfer = WithBothViews("UniformResourceLocator", Buffer(payload, [0]));

        Assert.Equal(expected, await ClipboardMonitorService.ReadSourceUrlAsync(transfer, codePage));
    }

    [Fact]
    public async Task WithOnlyAStringView_ThatStringIsUsed()
    {
        var item = new DataTransferItem();
        item.Set(DataFormat.CreateStringPlatformFormat("UniformResourceLocatorW"), Url);
        var transfer = new DataTransfer();
        transfer.Add(item);

        Assert.Equal(Url, await ClipboardMonitorService.ReadSourceUrlAsync(transfer));
    }

    [Fact]
    public async Task WithoutAnyUrlFormat_ThereIsNoSourceUrl()
    {
        var item = new DataTransferItem();
        item.SetText(Url);
        var transfer = new DataTransfer();
        transfer.Add(item);

        Assert.Null(await ClipboardMonitorService.ReadSourceUrlAsync(transfer));
    }
}
