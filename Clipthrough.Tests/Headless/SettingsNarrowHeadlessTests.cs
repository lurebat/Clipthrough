using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Controls;
using Clipthrough.Tests.Layout;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The hotkey rows in Settings had a fixed 260 DIP label column, so once the
/// window was fitted to a portrait screen the hotkey boxes slid over their
/// labels and their Record and Clear buttons ran past the edge. (B11)
/// </summary>
public sealed class SettingsNarrowHeadlessTests
{
    [AvaloniaTheory]
    [InlineData(576)]
    [InlineData(520)]
    public void HotkeyRows_DoNotOverlapAndStayInsideTheWindow(double width)
    {
        using var screenScope = WindowFitter.UseScreens(new TestScreens(TestScreens.Dip(width, 1149, 1.0)));
        using var harness = MainWindowTestHarness.Create();
        harness.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        var settings = harness.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        settings.Width = width;
        Dispatcher.UIThread.RunJobs();

        var grids = settings.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("hotkey-grid") && g.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(grids);
        var checkedRows = 0;
        foreach (var grid in grids)
        {
            foreach (var label in grid.Children.OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
            {
                var row = Grid.GetRow(label);
                foreach (var box in grid.Children.Where(c => c is HotkeyBox or TextBox && Grid.GetRow(c) == row && c.IsEffectivelyVisible))
                {
                    checkedRows++;
                    Assert.False(OnScreen.Overlaps(label, box, settings), $"'{label.Text}' overlaps its hotkey box at {width} DIP");

                    // A label that does not wrap is laid out at its column's width but
                    // draws past it, under the box, which bounds alone do not show.
                    Assert.True(
                        label.TextLayout.Width <= label.Bounds.Width + 0.5,
                        $"'{label.Text}' is {label.TextLayout.Width:F0} DIP of text in a {label.Bounds.Width:F0} DIP column at {width} DIP");
                    var right = box.TranslatePoint(new Point(box.Bounds.Width, 0), settings)!.Value.X;
                    Assert.True(right <= settings.Bounds.Width, $"the hotkey box for '{label.Text}' ends at x={right:F0} in a {settings.Bounds.Width:F0} DIP window");
                }
            }
        }

        Assert.True(checkedRows >= 4, $"only {checkedRows} hotkey rows were checked; the test is not looking at the rows it means to");
        settings.Close();
    }
}
