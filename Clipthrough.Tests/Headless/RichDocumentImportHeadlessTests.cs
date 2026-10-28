using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Controls;
using Clipthrough.Models;
using VellumText;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The preview's import cannot multiply or outlive itself (F-47). Live,
/// copying 1.2 KB of span-bomb HTML took the app from 0.6 GB to 3.7 GB,
/// retained, and a second copy to 9.1 GB: every selection imported twice, and
/// a timed-out import kept running beside the next one.
/// </summary>
public sealed class RichDocumentImportHeadlessTests
{
    private const string SpanBomb =
        "<table><tr>" +
        "<td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td>" +
        "<td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td>" +
        "<td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td>" +
        "<td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td>" +
        "<td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td>" +
        "<td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td>" +
        "<td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td>" +
        "<td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td><td colspan=1000 rowspan=1000>x</td>" +
        "</tr></table>";

    private sealed class RecordingImporter
    {
        private int _inFlight;
        private int _maxInFlight;

        public ConcurrentQueue<string> Imported { get; } = new();

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public int InFlight => Volatile.Read(ref _inFlight);

        public Func<string, bool>? BlockWhile { get; set; }

        public ManualResetEventSlim Gate { get; } = new(initialState: true);

        public Task<DocumentNode> Import(string markup, ClipContentFormat format)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            try
            {
                Imported.Enqueue(markup);
                if (BlockWhile?.Invoke(markup) == true)
                {
                    Gate.Wait(TimeSpan.FromSeconds(30));
                }

                return Task.FromResult(DocumentNode.FromParagraphs([markup]));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out waiting for the condition.");
    }

    [AvaloniaFact]
    public async Task ASpanBomb_FallsBackWithoutEverReachingTheImporter()
    {
        var importer = new RecordingImporter();
        var view = new RichDocumentView { Importer = importer.Import, ContentFormat = ClipContentFormat.Html, Markup = SpanBomb };

        await view.PendingRender;

        Assert.Empty(importer.Imported);
        Assert.True(view.FallbackText.IsVisible);
        Assert.False(view.Viewer.IsVisible);
    }

    [AvaloniaFact]
    public async Task AnOrdinaryTable_StillRendersAsADocument()
    {
        var table = "<table>" + string.Concat(Enumerable.Repeat("<tr><td>a</td><td colspan=2>b</td></tr>", 200)) + "</table>";
        var importer = new RecordingImporter();
        var view = new RichDocumentView { Importer = importer.Import, ContentFormat = ClipContentFormat.Html, Markup = table };

        await view.PendingRender;

        Assert.Single(importer.Imported);
        Assert.True(view.Viewer.IsVisible);
    }

    [Fact]
    public void CountTableCells_MultipliesSpansAndStopsPastTheLimit()
    {
        Assert.Equal(3, RichDocumentView.CountTableCells("<td>a</td><TH COLSPAN='2'>b</TH>", long.MaxValue));
        Assert.Equal(6, RichDocumentView.CountTableCells("<td rowspan=\"3\" colspan=\"2\">", long.MaxValue));
        Assert.True(RichDocumentView.CountTableCells(SpanBomb, RichDocumentView.MaxTableCells) > RichDocumentView.MaxTableCells);
        Assert.Equal(0, RichDocumentView.CountTableCells("<p>no tables, <tdx> is not a cell</p>", long.MaxValue));
    }

    /// <summary>
    /// Selecting a clip of another format changes both bound properties. Each
    /// used to start its own render, and the one that ran with the new format
    /// and the old markup imported too.
    /// </summary>
    [AvaloniaFact]
    public async Task OneSelectionChangingMarkupAndFormat_ImportsOnce()
    {
        var importer = new RecordingImporter();
        var view = new RichDocumentView { Importer = importer.Import, ContentFormat = ClipContentFormat.Html, Markup = "<p>first clip</p>" };
        await view.PendingRender;
        Assert.Single(importer.Imported);

        view.ContentFormat = ClipContentFormat.Rtf;
        view.Markup = @"{\rtf1\ansi second clip}";
        await view.PendingRender;

        Assert.Equal(2, importer.Imported.Count);
        Assert.Equal(@"{\rtf1\ansi second clip}", importer.Imported.Last());
    }

    /// <summary>
    /// The same content again - a selection that moves off and back within one
    /// turn, or a property re-raised with an equal value - reuses the import it
    /// already has rather than paying for it a second time.
    /// </summary>
    [AvaloniaFact]
    public async Task TheSameContentAgain_ReusesItsImport()
    {
        var importer = new RecordingImporter();
        var view = new RichDocumentView { Importer = importer.Import, ContentFormat = ClipContentFormat.Html, Markup = "<p>clip a</p>" };
        await view.PendingRender;

        view.Markup = "<p>clip b</p>";
        view.Markup = "<p>clip a</p>";
        await view.PendingRender;

        Assert.Equal(["<p>clip a</p>"], importer.Imported.ToArray());
        Assert.True(view.Viewer.IsVisible);
    }

    /// <summary>
    /// A timeout stops the preview waiting; it cannot stop the importer. A new
    /// selection must therefore wait for the abandoned import instead of
    /// running beside it, and a selection the user has already passed must not
    /// import at all.
    /// </summary>
    [AvaloniaFact]
    public async Task SelectionChangesDuringASlowImport_NeverRunTwoImportsAtOnce()
    {
        var importer = new RecordingImporter { BlockWhile = markup => markup.Contains("slow", StringComparison.Ordinal) };
        importer.Gate.Reset();
        var view = new RichDocumentView
        {
            Importer = importer.Import,
            ImportTimeout = TimeSpan.FromMilliseconds(100),
            ContentFormat = ClipContentFormat.Html,
            Markup = "<p>slow clip</p>",
        };

        await WaitUntilAsync(() => importer.InFlight == 1, TimeSpan.FromSeconds(5));
        await view.PendingRender; // the slow one times out and falls back
        Assert.True(view.FallbackText.IsVisible);

        view.Markup = "<p>passed over</p>";
        Dispatcher.UIThread.RunJobs();
        view.Markup = "<p>landed on</p>";
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, importer.MaxInFlight);

        importer.Gate.Set();
        await WaitUntilAsync(() => view.Viewer.IsVisible, TimeSpan.FromSeconds(5));

        Assert.Equal(1, importer.MaxInFlight);
        Assert.Equal(["<p>slow clip</p>", "<p>landed on</p>"], importer.Imported.ToArray());
    }
}
