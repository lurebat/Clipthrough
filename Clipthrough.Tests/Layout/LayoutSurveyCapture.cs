using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Models;
using Clipthrough.Tests.Headless;
using Clipthrough.ViewModels;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Layout;

/// <summary>
/// Renders every Clipthrough surface at a set of real-world working areas and
/// records which controls end up outside the screen. A survey tool for the
/// multi-monitor design (docs/plans/2026-10-28-001-multi-monitor-fit-design.md),
/// not a regression test: it asserts nothing and skips unless
/// CLIPTHROUGH_LAYOUT_CAPTURE_DIR names an output directory.
/// </summary>
/// <remarks>
/// The main window is modelled maximized (client = working area minus the
/// caption), which is the most room it can ever have. Owned windows are placed
/// the way Avalonia's CenterOwner does it: centred on the owner and clamped to
/// the working area only when they fit, so a dialog larger than the screen
/// overflows on both sides. A window cannot be laid out smaller than its
/// minimum size, so the rendered frame is what the window really lays out at;
/// the red overlay marks the part the screen cannot show.
/// </remarks>
public sealed class LayoutSurveyCapture
{
    public static string? OutputDirectory { get; } =
        Environment.GetEnvironmentVariable("CLIPTHROUGH_LAYOUT_CAPTURE_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>Subfolder of <see cref="OutputDirectory"/> the images go to.</summary>
    public static string Variant { get; } =
        Environment.GetEnvironmentVariable("CLIPTHROUGH_LAYOUT_CAPTURE_VARIANT") is { Length: > 0 } v ? v : "current";

    private const double CaptionHeight = 31;

    internal sealed record Profile(string Id, string Label, double WorkWidth, double WorkHeight, double Scaling);

    // Working areas in DIP: the screen in pixels divided by its scaling, less a
    // 48 DIP bottom taskbar.
    internal static readonly Profile[] Profiles =
    [
        new("p576", "1440x2992 @250% portrait", 576, 1149, 2.5),
        new("l853", "1280x720 @150% laptop", 853, 432, 1.5),
        new("w1366", "1366x768 @100%", 1366, 720, 1.0),
        new("w1536", "1920x1080 @125%", 1536, 816, 1.25),
        new("w1920", "1920x1080 @100%", 1920, 1032, 1.0),
        new("w2560", "2560x1440 @100%", 2560, 1392, 1.0),
        new("u3440", "3440x1440 @100% ultrawide", 3440, 1392, 1.0),
    ];

    [AvaloniaFact]
    public void Workspace()
    {
        SkipUnlessCapturing();
        foreach (var profile in Profiles)
        {
            using var harness = MainWindowTestHarness.Create();
            SeedRealisticClips(harness);
            harness.Notifications.PublishInfo("Copied to clipboard", "The selected clip is on the clipboard and ready to paste.");
            Dispatcher.UIThread.RunJobs();
            var placement = PlaceMainWindow(harness.Window, profile);
            Capture("workspace", profile, harness.Window, placement, WorkspaceKeys(harness));
            CaptureClipContextMenu(profile, harness);
        }
    }

    internal static (string Label, Control? Control)[] WorkspaceKeys(MainWindowTestHarness harness) =>
    [
        ("Search box", harness.SearchBox),
        ("Type filter: Files", FindByContent<ToggleButton>(harness.Window, "Files")),
        ("Sort", harness.Window.FindControl<ComboBox>("SortComboBox")),
        ("Search modifier: Semantic", FindByContent<CheckBox>(harness.Window, "Semantic")),
        ("Clip list", harness.ClipList),
        ("List: Delete checked", FindByCommand(harness.Window, harness.ViewModel.DeleteCheckedClipsCommand)),
        ("Preview: Copy", FindByCommand(harness.Window, harness.ViewModel.CopySelectedCommand)),
        ("Preview: Delete", FindByCommand(harness.Window, harness.ViewModel.DeleteSelectedCommand)),
        ("Preview: content", harness.Window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("content-stage") && b.IsEffectivelyVisible)),
        ("Footer: Settings", FindByCommand(harness.Window, harness.ViewModel.OpenSettingsCommand)),
        ("Toast", FindToast(harness.Window)),
    ];

    [AvaloniaFact]
    public async Task Welcome()
    {
        SkipUnlessCapturing();
        foreach (var profile in Profiles)
        {
            using var harness = MainWindowTestHarness.Create();
            await harness.ViewModel.InitializeAsync();
            Dispatcher.UIThread.RunJobs();
            var placement = PlaceMainWindow(harness.Window, profile);
            Capture("welcome", profile, harness.Window, placement,
            [
                ("Database path", harness.Window.FindControl<TextBox>("WelcomeDatabasePathTextBox")),
                ("Create library", FindByCommand(harness.Window, harness.ViewModel.SaveSettingsCommand)),
            ]);
        }
    }

    [AvaloniaFact]
    public void PasswordPrompt()
    {
        SkipUnlessCapturing();
        foreach (var profile in Profiles)
        {
            using var harness = MainWindowTestHarness.Create();
            harness.ViewModel.OpenPasswordPromptForTests();
            Dispatcher.UIThread.RunJobs();
            var placement = PlaceMainWindow(harness.Window, profile);
            Capture("password", profile, harness.Window, placement,
            [
                ("Password box", harness.Window.FindControl<TextBox>("PasswordPromptTextBox")),
            ]);
        }
    }

    [AvaloniaFact]
    public void Dialogs()
    {
        SkipUnlessCapturing();
        foreach (var profile in Profiles)
        {
            using var harness = MainWindowTestHarness.Create();
            SeedRealisticClips(harness);
            var owner = PlaceMainWindow(harness.Window, profile);

            harness.ViewModel.OpenSettingsCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            var settings = harness.Window.OwnedWindows.OfType<SettingsWindow>().Single();
            CaptureOwned("settings", profile, settings, owner);

            _ = RekeyDialog.ShowAsync(settings, harness.StorageOptions);
            Dispatcher.UIThread.RunJobs();
            var rekey = settings.OwnedWindows.OfType<RekeyDialog>().Single();
            CaptureOwned("rekey", profile, rekey, owner);
            rekey.Close();

            _ = ConfirmDialog.ShowAsync(settings, "Move the database?", "Clipthrough will copy the library to the new folder and switch to it. This can take a while on a large library.", "Move", "Cancel");
            Dispatcher.UIThread.RunJobs();
            var confirm = settings.OwnedWindows.OfType<ConfirmDialog>().Single();
            CaptureOwned("confirm", profile, confirm, owner);
            confirm.Close();
            settings.Close();

            var about = new AboutWindow { DataContext = harness.ViewModel };
            about.Show(harness.Window);
            Dispatcher.UIThread.RunJobs();
            CaptureOwned("about", profile, about, owner);
            about.Close();

            var help = new HelpWindow();
            help.Show(harness.Window);
            Dispatcher.UIThread.RunJobs();
            CaptureOwned("help", profile, help, owner);
            help.Close();

            var logs = new SessionLogsWindow { DataContext = harness.ViewModel.SessionLogs };
            logs.Show(harness.Window);
            Dispatcher.UIThread.RunJobs();
            CaptureOwned("logs", profile, logs, owner);
            logs.Close();

            var ai = new AiPromptWindow { DataContext = harness.ViewModel };
            ai.Show(harness.Window);
            Dispatcher.UIThread.RunJobs();
            CaptureOwned("aiprompt", profile, ai, owner);
            ai.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void SkipUnlessCapturing()
        => Assert.SkipWhen(OutputDirectory is null, "Layout survey capture: set CLIPTHROUGH_LAYOUT_CAPTURE_DIR to run.");

    internal static void SeedRealisticClips(MainWindowTestHarness harness)
    {
        var samples = new[]
        {
            "SELECT id, title, last_copied_at FROM clips WHERE is_favorite = 1 ORDER BY last_copied_at DESC LIMIT 50;",
            "https://github.com/lurebat/Clipthrough/pull/3",
            "Meeting notes: the 1.0 release needs the window to fit portrait and small laptop screens, and every dialog has to stay on screen.\n\n- welcome screen\n- workspace\n- settings and its dialogs",
            "dotnet test .\\Clipthrough.Tests\\Clipthrough.Tests.csproj --filter \"FullyQualifiedName!~HeadlessTests\"",
        };
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 40; i++)
        {
            var text = i < samples.Length ? samples[i] : $"Clip number {i + 1}: a line of copied text from a document or a chat";
            harness.ViewModel.Clips.Add(new ClipItemViewModel(new ClipEntry
            {
                Id = i + 1,
                Content = text,
                ContentBytes = Encoding.UTF8.GetBytes(text),
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                SourceApp = i % 3 == 0 ? "msedge" : i % 3 == 1 ? "Code" : "Teams",
                Hash = $"hash-{i + 1}",
                LastCopiedAt = now.AddMinutes(-i * 7),
                FirstCopiedAt = now.AddMinutes(-i * 7),
            }));
        }

        Dispatcher.UIThread.RunJobs();
        ReactiveUI.IReactiveObjectExtensions.RaisePropertyChanged(harness.ViewModel, nameof(MainWindowViewModel.HasNoClips));
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[2];
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Sizes the main window as if maximized on <paramref name="profile"/>'s
    /// working area. Returns the client rectangle in working-area DIP, which
    /// is larger than the working area when a minimum size wins.
    /// </summary>
    internal static Rect PlaceMainWindow(Window window, Profile profile)
    {
        window.SetRenderScaling(profile.Scaling);
        window.Width = profile.WorkWidth;
        window.Height = profile.WorkHeight - CaptionHeight;
        Dispatcher.UIThread.RunJobs();
        return new Rect(0, CaptionHeight, window.Bounds.Width, window.Bounds.Height);
    }

    internal static void Capture(string surface, Profile profile, Window window, Rect clientRect, IEnumerable<(string Label, Control? Control)> keys)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        var results = keys.Select(k => Classify(k.Label, k.Control, window, clientRect, profile)).ToList();
        SaveScreenView(surface, profile, window.Title ?? surface, frame, clientRect);
        Report(surface, profile, clientRect, results, window);
    }

    private static void CaptureOwned(string surface, Profile profile, Window dialog, Rect ownerClient)
    {
        dialog.SetRenderScaling(profile.Scaling);
        Dispatcher.UIThread.RunJobs();
        var size = dialog.Bounds.Size;
        var ownerFrame = new Rect(ownerClient.X, ownerClient.Y - CaptionHeight, ownerClient.Width, ownerClient.Height + CaptionHeight);
        var frameSize = new Size(size.Width, size.Height + CaptionHeight);
        var x = ownerFrame.X + ((ownerFrame.Width - frameSize.Width) / 2);
        var y = ownerFrame.Y + ((ownerFrame.Height - frameSize.Height) / 2);
        if (frameSize.Width <= profile.WorkWidth)
        {
            x = Math.Clamp(x, 0, profile.WorkWidth - frameSize.Width);
        }

        if (frameSize.Height <= profile.WorkHeight)
        {
            y = Math.Clamp(y, 0, profile.WorkHeight - frameSize.Height);
        }

        // The prototype fits every window into its working area when it opens
        // (WorkingAreaFit). Headless has no real screen to fit against, so run
        // the same geometry here against the modelled one.
        if (Variant != "current")
        {
            var s = profile.Scaling;
            var fit = MainWindow.FitToWorkingArea(
                size,
                new Size(0, CaptionHeight),
                new PixelPoint((int)Math.Round(x * s), (int)Math.Round(y * s)),
                new PixelRect(0, 0, (int)Math.Floor(profile.WorkWidth * s), (int)Math.Floor(profile.WorkHeight * s)),
                s);
            if (fit is { } placement)
            {
                if (placement.Size.Height < size.Height)
                {
                    dialog.SizeToContent &= ~SizeToContent.Height;
                }

                dialog.Width = placement.Size.Width;
                dialog.Height = placement.Size.Height;
                Dispatcher.UIThread.RunJobs();
                size = dialog.Bounds.Size;
                x = placement.Position.X / s;
                y = placement.Position.Y / s;
            }
        }

        var clientRect = new Rect(x, y + CaptionHeight, size.Width, size.Height);
        // The dialog's own action buttons: those outside any scroller, which the
        // user cannot bring into view by scrolling.
        var keys = dialog.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible
                && !(b.Name ?? string.Empty).StartsWith("PART_", StringComparison.Ordinal)
                && !b.GetVisualAncestors().OfType<ScrollViewer>().Any())
            .Select(b => (LabelOf(b), (Control?)b))
            .ToList();
        Capture(surface, profile, dialog, clientRect, keys);
    }

    private static void CaptureClipContextMenu(Profile profile, MainWindowTestHarness harness)
    {
        var list = harness.ClipList;
        var menu = list.ContextMenu!;
        menu.Open(list);
        Dispatcher.UIThread.RunJobs();
        var menuSize = menu.Bounds.Size;
        AppendReport(string.Create(CultureInfo.InvariantCulture,
            $"| contextmenu | {profile.Label} | {profile.WorkWidth:F0}x{profile.WorkHeight:F0} | menu {menuSize.Width:F0}x{menuSize.Height:F0} | {(menuSize.Height > profile.WorkHeight ? "TALLER THAN WORK AREA" : "fits")} | |"));
        menu.Close();
        Dispatcher.UIThread.RunJobs();
    }

    internal enum Reach
    {
        Visible,
        ScrollReachable,
        Clipped,
        OffScreen,
        Missing,
    }

    internal sealed record KeyResult(string Label, Reach Reach, double VisibleFraction);

    /// <summary>
    /// How much of a control the user can see: clipped by any ancestor that
    /// clips (a scroller viewport, a ClipToBounds border), then by the working
    /// area. A control hidden only by a scroller that can scroll is reachable.
    /// </summary>
    internal static KeyResult Classify(string label, Control? control, Window window, Rect clientRect, Profile profile)
    {
        if (control is null || !control.IsEffectivelyVisible || control.TranslatePoint(default, window) is not { } topLeft)
        {
            return new KeyResult(label, Reach.Missing, 0);
        }

        var rect = new Rect(topLeft, control.Bounds.Size);
        var visible = rect;
        var inScrollableViewer = false;
        foreach (var ancestor in control.GetVisualAncestors())
        {
            if (ancestor is ScrollViewer sv && (sv.Extent.Width > sv.Viewport.Width + 0.5 || sv.Extent.Height > sv.Viewport.Height + 0.5))
            {
                inScrollableViewer = true;
            }

            if (ancestor.ClipToBounds && ancestor.TranslatePoint(default, window) is { } at)
            {
                visible = visible.Intersect(new Rect(at, ancestor.Bounds.Size));
            }
        }

        var area = rect.Width * rect.Height;
        var screen = visible.Translate(clientRect.Position).Intersect(new Rect(0, 0, profile.WorkWidth, profile.WorkHeight));
        var fraction = area <= 0 ? 0 : Math.Max(0, screen.Width) * Math.Max(0, screen.Height) / area;
        var clippedInWindow = visible.Width * visible.Height < area - 1;

        var reach = fraction >= 0.99 ? Reach.Visible
            : clippedInWindow && inScrollableViewer ? Reach.ScrollReachable
            : clippedInWindow ? Reach.Clipped
            : Reach.OffScreen;
        return new KeyResult(label, reach, fraction);
    }

    private static void Report(string surface, Profile profile, Rect clientRect, List<KeyResult> results, Window window)
    {
        var overflowX = Math.Max(0, clientRect.Right - profile.WorkWidth) + Math.Max(0, -clientRect.X);
        var overflowY = Math.Max(0, clientRect.Bottom - profile.WorkHeight) + Math.Max(0, CaptionHeight - clientRect.Y);
        var broken = results.Where(r => r.Reach != Reach.Visible)
            .Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Label}: {r.Reach} ({r.VisibleFraction:P0})"))
            .ToList();
        var interactive = window.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Button or CheckBox or RadioButton or TextBox or ComboBox or ToggleButton && c.IsEffectivelyVisible)
            .Select(c => Classify(LabelOf(c), c, window, clientRect, profile))
            .ToList();
        var lostLabels = interactive
            .Where(r => r.Reach is Reach.OffScreen or Reach.Clipped && !r.Label.StartsWith("PART_", StringComparison.Ordinal))
            .Select(r => r.Label)
            .Distinct()
            .Take(10)
            .ToList();
        var lost = interactive.Count(r => r.Reach is Reach.OffScreen or Reach.Clipped && !r.Label.StartsWith("PART_", StringComparison.Ordinal));
        AppendReport(string.Create(CultureInfo.InvariantCulture,
            $"| {surface} | {profile.Label} | {profile.WorkWidth:F0}x{profile.WorkHeight:F0} | client {clientRect.Width:F0}x{clientRect.Height:F0} at ({clientRect.X:F0},{clientRect.Y:F0}); overflow x {overflowX:F0}, y {overflowY:F0} | lost {lost}/{interactive.Count}: {string.Join(", ", lostLabels)} | {(broken.Count > 0 ? string.Join("; ", broken) : "all key controls visible")} |"));
    }

    private static void AppendReport(string line)
        => File.AppendAllText(Path.Combine(OutputDirectory!, $"measurements-{Variant}.md"), line + "\n");

    /// <summary>
    /// Composes the window's frame onto the working area: the screen in dark
    /// grey with a green edge, a mock caption, and red over whatever falls
    /// outside the screen.
    /// </summary>
    private static void SaveScreenView(string surface, Profile profile, string title, WriteableBitmap? frame, Rect clientRect)
    {
        if (frame is null)
        {
            return;
        }

        var work = new Rect(0, 0, profile.WorkWidth, profile.WorkHeight);
        var windowRect = new Rect(clientRect.X, clientRect.Y - CaptionHeight, clientRect.Width, clientRect.Height + CaptionHeight);
        var canvas = work.Union(windowRect);
        const double LabelHeight = 28;
        var size = new PixelSize((int)Math.Ceiling(canvas.Width), (int)Math.Ceiling(canvas.Height + LabelHeight));
        using var target = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        {
            using (ctx.PushTransform(Matrix.CreateTranslation(-canvas.X, -canvas.Y + LabelHeight)))
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(90, 30, 30)), canvas);
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(36, 36, 40)), work);
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), new Rect(windowRect.X, windowRect.Y, windowRect.Width, CaptionHeight));
                ctx.DrawText(Text(title, 12, Brushes.Gainsboro), new Point(windowRect.X + 10, windowRect.Y + 8));
                ctx.DrawImage(frame, new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height), clientRect);

                var red = new SolidColorBrush(Color.FromArgb(120, 230, 40, 40));
                foreach (var outside in Outside(windowRect, work))
                {
                    ctx.FillRectangle(red, outside);
                }

                ctx.DrawRectangle(new Pen(Brushes.LimeGreen, 3), work.Deflate(1.5));
            }

            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, size.Width, LabelHeight));
            ctx.DrawText(
                Text(string.Create(CultureInfo.InvariantCulture, $"{surface} - {profile.Label} - work area {profile.WorkWidth:F0}x{profile.WorkHeight:F0} DIP - window client {clientRect.Width:F0}x{clientRect.Height:F0}"), 14, Brushes.White),
                new Point(8, 5));
        }

        var dir = Path.Combine(OutputDirectory!, Variant);
        Directory.CreateDirectory(dir);
        target.Save(Path.Combine(dir, $"{surface}-{profile.Id}.png"), new PngBitmapEncoderOptions());
    }

    private static IEnumerable<Rect> Outside(Rect rect, Rect work)
    {
        if (rect.X < work.X) yield return new Rect(rect.X, rect.Y, work.X - rect.X, rect.Height);
        if (rect.Right > work.Right) yield return new Rect(work.Right, rect.Y, rect.Right - work.Right, rect.Height);
        var midX = Math.Max(rect.X, work.X);
        var midW = Math.Min(rect.Right, work.Right) - midX;
        if (rect.Y < work.Y) yield return new Rect(midX, rect.Y, midW, work.Y - rect.Y);
        if (rect.Bottom > work.Bottom) yield return new Rect(midX, work.Bottom, midW, rect.Bottom - work.Bottom);
    }

    private static FormattedText Text(string text, double size, IBrush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    internal static string LabelOf(Control control)
    {
        if (!string.IsNullOrEmpty(control.Name)) return control.Name;
        if (AutomationProperties.GetName(control) is { Length: > 0 } automationName) return automationName;
        return control switch
        {
            ContentControl { Content: string s } => s,
            TextBox { PlaceholderText: { Length: > 0 } p } => $"TextBox '{(p.Length > 28 ? p[..28] + "..." : p)}'",
            _ => control.GetType().Name,
        };
    }

    internal static T? FindByContent<T>(Window window, string content)
        where T : ContentControl
        => window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => Equals(c.Content, content) && c.IsEffectivelyVisible);

    internal static Button? FindByCommand(Window window, System.Windows.Input.ICommand command)
        => window.GetVisualDescendants().OfType<Button>()
            .LastOrDefault(b => ReferenceEquals(b.Command, command) && b.IsEffectivelyVisible);

    internal static Control? FindToast(Window window)
        => window.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.DataContext is AppNotificationViewModel && b.CornerRadius.TopLeft >= 10 && b.IsEffectivelyVisible);
}
