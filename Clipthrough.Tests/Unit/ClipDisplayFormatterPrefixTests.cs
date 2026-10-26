using System;
using System.Linq;
using System.Text;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// F-10. Building a list row formatted the whole clip on the UI thread - Split
/// and CollapseWhitespace over all of it - to show 88-140 characters: 40 ms and
/// 20.7 MB for a 1.9 MB log, up to 733 ms for a page. Only a prefix can affect
/// those strings, so only a prefix is formatted, whenever that provably gives
/// the same strings as the full text.
/// </summary>
public sealed class ClipDisplayFormatterPrefixTests
{
    private static ClipEntry Text(string content) => new() { Id = 1, Content = content, ContentType = ContentType.Text, Hash = "h" };

    private static string Log(int length)
    {
        var sb = new StringBuilder(length + 80);
        for (var i = 0; sb.Length < length; i++)
        {
            sb.Append("2025-03-01 08:00:00.").Append(i % 1000).Append(" [INFO] worker-").Append(i % 32).Append(" message number ").Append(i).Append('\n');
        }

        return sb.ToString(0, length);
    }

    private static ClipDisplayStrings Unbounded(string content)
        => new(
            ClipDisplayFormatter.BuildTitle(content, ContentType.Text),
            ClipDisplayFormatter.BuildPreviewSnippet(content, ContentType.Text),
            ClipDisplayFormatter.BuildSingleLinePreview(content, ContentType.Text));

    [Fact]
    public void ATwoMegabyteClip_ShowsWhatItsFirstFourKilobytesShow()
    {
        var content = Log(2 * 1024 * 1024);

        var strings = ClipDisplayFormatter.BuildDisplayStrings(Text(content));

        Assert.Equal(Unbounded(content[..4096]), strings);
        Assert.Equal(Unbounded(content), strings);
    }

    [Fact]
    public void ATwoMegabyteClip_IsFormattedWithoutTouchingAllOfIt()
    {
        var clip = Text(Log(2 * 1024 * 1024));
        ClipDisplayFormatter.BuildDisplayStrings(clip); // warm-up: regex and JIT

        var before = GC.GetAllocatedBytesForCurrentThread();
        ClipDisplayFormatter.BuildDisplayStrings(clip);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 256 * 1024, $"Formatting a 2 MB clip allocated {allocated / 1024} KB; the unbounded path allocates several MB.");
    }

    /// <summary>
    /// Where a prefix would not decide the strings - nothing but blank lines in
    /// it, or a first line that has not ended yet and is still short - the full
    /// text is used, so the row never shows something the clip does not start with.
    /// </summary>
    [Theory]
    [InlineData(5000, "first real words after a long blank run")]
    [InlineData(0, "short")]
    public void WhenAPrefixCannotDecide_TheResultIsStillTheFullTextsResult(int leadingBlankLines, string tail)
    {
        var content = new string('\n', leadingBlankLines) + new string(' ', 4090) + tail + "\n" + Log(10_000);

        Assert.Equal(Unbounded(content), ClipDisplayFormatter.BuildDisplayStrings(Text(content)));
    }

    [Fact]
    public void APrefixEndingInsideASurrogatePair_IsNotCutInHalf()
    {
        var content = new string('a', 4095) + "\U0001F600" + Log(10_000);

        var strings = ClipDisplayFormatter.BuildDisplayStrings(Text(content));

        Assert.Equal(Unbounded(content), strings);
    }
}