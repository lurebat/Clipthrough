using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Tests.Layout;
using Clipthrough.ViewModels;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The workspace at each breakpoint, from a 576 DIP portrait screen to a
/// 3440 DIP ultrawide. Until the window reflowed it was held at 1100x640 DIP,
/// so below that the right half of the workspace - search, filters, preview,
/// Settings, every toast - was off the screen.
/// (docs/plans/2026-10-28-001-multi-monitor-fit-design.md)
/// </summary>
/// <remarks>
/// The window fills a working area of the same size from its top-left, the
/// way a maximized window does; every assertion is against that working area,
/// not the window, which a minimum size could hold larger than the screen.
/// </remarks>
public sealed class ResponsiveWorkspaceHeadlessTests
{
    public static TheoryData<double, double> Sizes => new()
    {
        { 576, 1118 },   // 1440x2992 at 250%, portrait: Narrow
        { 853, 401 },    // 1280x720 at 150%: Medium, Short
        { 1000, 700 },   // Medium
        { 480, 360 },    // the window minimum: Narrow, Short
        { 1366, 689 },   // 1366x768 at 100%: Wide
        { 3440, 1361 },  // ultrawide: Wide
    };

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public void EveryKeyControlIsOnScreen(double width, double height)
    {
        using var harness = Open(width, height, out var area);
        var window = harness.Window;
        var vm = harness.ViewModel;
        var isShort = height < WorkspaceBreakpoints.ShortBelowHeight;

        // Narrow and short together scroll the workspace (see MainWindow.axaml),
        // so there "reachable" means brought into view by scrolling to it.
        var scrolls = width < WorkspaceBreakpoints.NarrowBelowWidth && isShort;
        void Check(string label, Control? control)
        {
            if (scrolls)
            {
                OnScreen.AssertReachable(label, control, window, area);
            }
            else
            {
                OnScreen.AssertVisible(label, control, window, area);
            }
        }

        Check("menu", window.FindControl<Menu>("TopMenu"));
        Check("search box", harness.SearchBox);
        foreach (var type in new[] { "All", "Text", "Image", "Rich", "Files" })
        {
            Check($"type filter {type}", OnScreen.FindByContent<ToggleButton>(window, type));
        }

        foreach (var category in new[] { vm.FavoritesFilterLabel, vm.SensitiveFilterLabel, vm.PastedFilterLabel })
        {
            Check($"category filter {category}", OnScreen.FindByContent<CheckBox>(window, category));
        }

        Check("sort order", window.FindControl<ComboBox>("SortComboBox"));
        if (isShort)
        {
            Check("Search options", window.FindControl<Button>("SearchOptionsButton"));
        }
        else
        {
            foreach (var modifier in new[] { vm.RegexFilterLabel, vm.CaseSensitiveFilterLabel, vm.WildcardFilterLabel, vm.WholeWordFilterLabel, "Fuzzy", "Semantic" })
            {
                Check($"search modifier {modifier}", OnScreen.FindByContent<CheckBox>(window, modifier));
            }

            Check("footer Settings", OnScreen.FindByCommand(window, vm.OpenSettingsCommand));
            Check("footer Logs", OnScreen.FindByCommand(window, vm.SessionLogs.OpenCommand));
        }

        Check("list Delete checked", OnScreen.FindByCommand(window, vm.DeleteCheckedClipsCommand));
        Check("preview Copy", OnScreen.FindByCommand(window, vm.CopySelectedCommand));
        Check("preview Delete", OnScreen.FindByCommand(window, vm.DeleteSelectedCommand));

        // On screen is not enough: a button that runs past its pane is drawn
        // under the pane beside it.
        var listPane = window.FindControl<Border>("ClipListPane")!;
        var previewPane = window.FindControl<Border>("PreviewPane")!;
        AssertWithin("list Delete checked", OnScreen.FindByCommand(window, vm.DeleteCheckedClipsCommand)!, listPane, window);
        AssertWithin("preview Copy", OnScreen.FindByCommand(window, vm.CopySelectedCommand)!, previewPane, window);
        AssertWithin("preview Delete", OnScreen.FindByCommand(window, vm.DeleteSelectedCommand)!, previewPane, window);

        var visibleRows = harness.ClipList.GetVisualDescendants().OfType<ListBoxItem>()
            .Count(item => OnScreen.Classify(item, window, default, area).Reach is Reach.Visible or Reach.ScrollReachable);
        Assert.True(visibleRows >= 2, $"only {visibleRows} clip rows are wholly visible at {width}x{height}");

        var stage = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("content-stage") && b.IsEffectivelyVisible);
        Check("preview", stage);
        var (stageReach, _) = OnScreen.Classify(stage, window, default, area);
        Assert.True(stageReach is Reach.Visible, $"the preview is {stageReach} at {width}x{height}");
        Assert.True(stage.Bounds.Height >= 40, $"the preview is {stage.Bounds.Height:F0} DIP tall at {width}x{height}");
    }

    [AvaloniaTheory]
    [InlineData(1366, 689)]
    [InlineData(1920, 1001)]
    public void Wide_IsTheLayoutItAlwaysWas(double width, double height)
    {
        using var harness = Open(width, height, out _);
        var window = harness.Window;

        Assert.Equal((WorkspaceWidth.Wide, false), window.AppliedBreakpoint);
        Assert.True(window.FindControl<TextBlock>("HeroTitle")!.IsEffectivelyVisible, "the title is hidden in Wide");

        // List and preview side by side at 1:2.
        var list = window.FindControl<Border>("ClipListPane")!;
        var preview = window.FindControl<Border>("PreviewPane")!;
        Assert.Equal(list.Bounds.Y, preview.Bounds.Y);
        Assert.InRange(preview.Bounds.Width - (list.Bounds.Width * 2), -1, 1);

        // Search and type toggles share a row; category filters, sort and the
        // search modifiers share the next.
        AssertSameRow(harness.SearchBox, window.FindControl<Control>("HeroTypeSegments")!, window);
        AssertSameRow(window.FindControl<Control>("CategoryFilters")!, window.FindControl<Control>("SearchModifiers")!, window);
        Assert.False(window.FindControl<Button>("SearchOptionsButton")!.IsEffectivelyVisible, "Search options shows in Wide");
        Assert.True(window.FindControl<Border>("FilterSeparator")!.IsEffectivelyVisible, "the filter separator is hidden in Wide");
    }

    [AvaloniaFact]
    public void Narrow_StacksThePreviewUnderTheListWithAHorizontalSplitter()
    {
        using var harness = Open(576, 1118, out _);
        var window = harness.Window;
        var list = window.FindControl<Border>("ClipListPane")!;
        var preview = window.FindControl<Border>("PreviewPane")!;
        var splitter = window.FindControl<GridSplitter>("WorkspaceSplitter")!;

        Assert.True(preview.Bounds.Y >= list.Bounds.Bottom, $"the preview starts at y={preview.Bounds.Y:F0}, the list ends at {list.Bounds.Bottom:F0}");
        Assert.Equal(GridResizeDirection.Rows, splitter.ResizeDirection);
        Assert.True(splitter.Bounds.Y >= list.Bounds.Bottom - 0.5 && splitter.Bounds.Bottom <= preview.Bounds.Y + 0.5, "the splitter is not between the list and the preview");
    }

    [AvaloniaFact]
    public void Short_HidesTheFooterAndCountersAndCollapsesTheModifiers()
    {
        using var harness = Open(853, 401, out _);
        var window = harness.Window;

        Assert.False(window.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("footer-panel") && b.IsEffectivelyVisible), "the footer is showing");
        Assert.False(window.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("list-stats") && b.IsEffectivelyVisible), "the list counters are showing");
        Assert.False(window.FindControl<WrapPanel>("SearchModifiers")!.IsEffectivelyVisible, "the modifiers are showing beside Search options");
    }

    [AvaloniaFact]
    public void SearchOptions_ChangesTheSameSearchAsTheModifiers()
    {
        using var harness = Open(853, 401, out _);
        var button = harness.Window.FindControl<Button>("SearchOptionsButton")!;
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();

        var panel = harness.Window.FindControl<StackPanel>("SearchOptionsPanel")!;
        var regex = panel.Children.OfType<CheckBox>().Single(c => Equals(c.Content, harness.ViewModel.RegexFilterLabel));
        Assert.False(harness.ViewModel.UseRegexSearch);
        regex.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.ViewModel.UseRegexSearch);
        Assert.Equal(6, panel.Children.OfType<CheckBox>().Count());
        button.Flyout.Hide();
    }

    [AvaloniaFact]
    public void Ultrawide_CapsTheSearchTogglesAndPreview()
    {
        using var harness = Open(3440, 1361, out _);
        var window = harness.Window;

        Assert.True(harness.SearchBox.Bounds.Width <= WorkspaceBreakpoints.SearchMaxWidth + 1, $"search is {harness.SearchBox.Bounds.Width:F0} DIP");
        var segments = window.FindControl<Control>("HeroTypeSegments")!;
        Assert.True(segments.Bounds.Width <= WorkspaceBreakpoints.TypeFilterMaxWidth + 1, $"the type toggles are {segments.Bounds.Width:F0} DIP");
        var segmentsRight = segments.TranslatePoint(new Point(segments.Bounds.Width, 0), window)!.Value.X;
        Assert.True(segmentsRight >= window.Bounds.Width - 40, $"the type toggles end at x={segmentsRight:F0}, short of the right edge");
        Assert.True(window.FindControl<Border>("PreviewPane")!.Bounds.Width <= WorkspaceBreakpoints.PreviewMaxWidth, "the preview is not capped");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public void Toasts_SitBottomRight_ClearOfTheToolbar(double width, double height)
    {
        using var harness = Open(width, height, out var area);
        harness.Notifications.PublishInfo("Copied to clipboard", "The selected clip is on the clipboard and ready to paste.");
        Dispatcher.UIThread.RunJobs();
        var window = harness.Window;
        var toast = window.GetVisualDescendants().OfType<Border>().Single(b => b.DataContext is AppNotificationViewModel && b.CornerRadius.TopLeft >= 10);

        OnScreen.AssertVisible("toast", toast, window, area);
        var bottomRight = toast.TranslatePoint(new Point(toast.Bounds.Width, toast.Bounds.Height), window)!.Value;
        Assert.True(bottomRight.X >= window.Bounds.Width - 40, $"the toast ends at x={bottomRight.X:F0} in a {window.Bounds.Width:F0} DIP window");
        Assert.True(bottomRight.Y >= window.Bounds.Height / 2, $"the toast ends at y={bottomRight.Y:F0} in a {window.Bounds.Height:F0} DIP window");
        Assert.False(OnScreen.Overlaps(toast, window.FindControl<Control>("HeroTypeSegments")!, window), "the toast covers the type toggles");
    }

    private static MainWindowTestHarness Open(double width, double height, out Size area)
    {
        area = new Size(width, height);
        var scope = WindowFitter.UseScreens(new TestScreens(TestScreens.Dip(width, height, 1.0)));
        var harness = MainWindowTestHarness.Create();
        scope.Dispose();
        LayoutSurveyCapture.SeedRealisticClips(harness);
        harness.Window.Position = new PixelPoint(0, 0);
        harness.Window.Width = width;
        harness.Window.Height = height;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new Size(width, height), harness.Window.Bounds.Size);
        return harness;
    }

    private static void AssertWithin(string label, Control control, Control container, Window window)
    {
        var inner = new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
        var outer = new Rect(container.TranslatePoint(default, window)!.Value, container.Bounds.Size);
        Assert.True(
            outer.Inflate(0.5).Contains(inner),
            $"{label} spans {inner} outside {container.Name} at {outer}");
    }

    private static void AssertSameRow(Control a, Control b, Window window)
    {
        var ya = a.TranslatePoint(default, window)!.Value.Y + (a.Bounds.Height / 2);
        var yb = b.TranslatePoint(default, window)!.Value.Y + (b.Bounds.Height / 2);
        Assert.True(System.Math.Abs(ya - yb) < 12, $"{a.Name ?? a.GetType().Name} is centred at y={ya:F0}, {b.Name ?? b.GetType().Name} at {yb:F0}");
    }
}
