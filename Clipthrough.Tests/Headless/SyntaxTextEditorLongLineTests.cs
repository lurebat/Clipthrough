using System;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Controls;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// A clip that is one enormous line must not be laid out in full.
/// </summary>
/// <remarks>
/// AvaloniaEdit virtualises per document line, so many lines are cheap at any
/// size while a single long line defeats virtualisation entirely: the one visual
/// line covering the viewport is laid out whole, on the UI thread, on every
/// selection change. Measured in review round 2 at ~18 us per character, linear
/// across three doublings. Minified JSON, a base64 blob, a one-line SQL
/// statement or a log line all land here, and 852 of the reporting user's 1,638
/// clips take this path.
///
/// The guard shortens rather than break-inserts, and forces read-only so the
/// stand-in can never be committed over the clip it stands in for.
/// </remarks>
public sealed class SyntaxTextEditorLongLineTests
{
    private static (SyntaxTextEditor Editor, Window Window) Show(string text, bool readOnly = false)
    {
        var editor = new SyntaxTextEditor { IsReadOnly = readOnly };
        var window = new Window { Content = editor, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        editor.Text = text;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (editor, window);
    }

    private static string OneLine(int chars) => new('x', chars);

    private static string ManyLines(int chars)
    {
        var sb = new StringBuilder(chars + 64);
        while (sb.Length < chars) { sb.Append("the quick brown fox jumps over the lazy dog\n"); }
        return sb.ToString(0, chars);
    }

    /// <summary>
    /// The control: ordinary multi-line content of the same size is untouched
    /// and stays editable. Without this, "always shorten" would pass below.
    /// </summary>
    [AvaloniaFact]
    public void MultiLineContentIsShownWholeAndStaysEditable()
    {
        var (editor, window) = Show(ManyLines(60_000));
        try
        {
            Assert.Equal(60_000, editor.Text!.Length);
            Assert.False(editor.IsShowingShortenedText, "ordinary multi-line content must not be shortened");
            Assert.False(editor.IsEffectivelyReadOnly);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// A single long line is shortened and the pane goes read-only, so the
    /// partial view cannot be saved over the real clip.
    /// </summary>
    [AvaloniaFact]
    public void ASingleEnormousLineIsShortenedAndReadOnly()
    {
        var (editor, window) = Show(OneLine(60_000));
        try
        {
            // The bound property still carries the whole clip - only what is
            // handed to the layout engine is shortened.
            Assert.Equal(60_000, editor.Text!.Length);
            Assert.True(editor.IsShowingShortenedText, "the long-line guard did not fire");
            Assert.True(editor.IsEffectivelyReadOnly, "a shortened view must not be editable, or a save would truncate the clip");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// The point of the whole change: showing a one-line clip must not cost
    /// time proportional to its length.
    /// </summary>
    /// <remarks>
    /// Asserts a ratio between two sizes rather than an absolute millisecond
    /// budget, so it does not become a flaky speed test on a loaded machine.
    /// Before the guard the cost was linear - 2.00x per doubling across three
    /// measured doublings - so an 8x size increase cost about 8x the time.
    /// </remarks>
    [AvaloniaFact]
    public void ShowingAOneLineClipDoesNotScaleWithItsLength()
    {
        var small = Measure(OneLine(25_000));
        var large = Measure(OneLine(200_000));

        Assert.True(
            large < small * 4,
            $"cost still scales with length: {small:F1} ms at 25k chars vs {large:F1} ms at 200k (8x the text). "
                + "The long-line guard is not taking effect.");
    }

    private static double Measure(string text)
    {
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var editor = new SyntaxTextEditor();
            var window = new Window { Content = editor, Width = 400, Height = 300 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var sw = Stopwatch.StartNew();
            editor.Text = text;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            sw.Stop();

            window.Close();
            Dispatcher.UIThread.RunJobs();

            // Minimum of repeats: the mean is dominated by whatever else the
            // host is doing, and a floor cannot be inflated by interference.
            if (sw.Elapsed.TotalMilliseconds < best) { best = sw.Elapsed.TotalMilliseconds; }
        }

        return best;
    }
}