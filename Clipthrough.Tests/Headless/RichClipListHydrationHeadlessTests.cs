using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Clipthrough.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// A rich clip must keep its formatting after a round trip through the clip
/// list. List and search queries select <c>NULL</c> for <c>content_bytes</c> so
/// a 300-row refresh does not materialise every blob, and rich text keeps its
/// HTML/RTF there - the same column as an image payload.
/// </summary>
/// <remarks>
/// Hydration existed for exactly this, but its guard was
/// <c>ContentType == Image &amp;&amp; ContentBytes is null</c>, so it declined
/// to fetch anything else. Every rich clip read back from the list therefore had
/// no markup: <c>GetRawMarkup</c> returned null, <c>FullContent</c> fell back to
/// the plain-text field, and Copy handed that to the rich-content writer. Asaf
/// reported it as "after pressing copy on a rich text the formatting was gone".
///
/// The freshly captured clip is unaffected - it arrives through the capture
/// stream carrying its bytes - which is why this survived: it reproduces only
/// on a clip that has been round-tripped, and never on the one you just copied.
/// AvaloniaFact, not Fact: hydration completes through Dispatcher.UIThread.InvokeAsync,
/// which without a dispatcher never returns - so a plain Fact hangs rather than
/// fails, and a hang produces no assertion text to read.
/// </remarks>
public sealed class RichClipListHydrationHeadlessTests
{
    private const string Markup =
        "<html><body><!--StartFragment--><p style=\"color:red\"><b>Quarterly</b> revenue rose 12%.</p><!--EndFragment--></body></html>";

    private const string PlainText = "Quarterly revenue rose 12%.";

    private static async Task<ClipEntry> CaptureRichAsync(TemporaryDatabaseScope scope)
    {
        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.RichText,
            ContentFormat = ClipContentFormat.Html,
            ContentText = PlainText,
            ContentBytes = Encoding.UTF8.GetBytes(Markup),
        });

        Assert.NotNull(clip);
        return clip!;
    }

    /// <summary>
    /// Establishes the premise the regression rests on: the row really does come
    /// back without its markup, so the test below is about hydration and not
    /// about a fixture that was rich all along.
    /// </summary>
    [AvaloniaFact]
    public async Task AListRow_ArrivesWithoutItsMarkup()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureRichAsync(scope);

        var listed = (await scope.ClipStoreService.SearchAsync(new ClipSearchFilters())).Items.Single();

        Assert.Null(listed.ContentBytes);
        Assert.Null(ClipDisplayFormatter.GetRawMarkup(listed));
    }

    [AvaloniaFact]
    public async Task HydratingAListRow_RestoresTheMarkup()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureRichAsync(scope);

        var listed = (await scope.ClipStoreService.SearchAsync(new ClipSearchFilters())).Items.Single();
        var item = new ClipItemViewModel(listed, contentHydrator: id => scope.ClipStoreService.GetByIdAsync(id));

        Assert.True(await item.EnsureContentHydratedAsync(), "hydration declined to fetch a rich clip's bytes");

        Assert.Equal(Markup, ClipDisplayFormatter.GetRawMarkup(item.Clip));
        Assert.Contains("<b>", item.FullContent, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The control. A plain-text clip has nothing in content_bytes worth
    /// fetching, so hydration must still decline - otherwise every arrow key
    /// buys a database round trip and rebuilds the preview for nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task APlainTextListRow_IsStillNotHydrated()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = PlainText,
            ContentBytes = Encoding.UTF8.GetBytes(PlainText),
        });

        var listed = (await scope.ClipStoreService.SearchAsync(new ClipSearchFilters())).Items.Single();
        var item = new ClipItemViewModel(listed, contentHydrator: id => scope.ClipStoreService.GetByIdAsync(id));

        Assert.False(await item.EnsureContentHydratedAsync(), "a plain-text row was hydrated for no benefit");
    }
}
