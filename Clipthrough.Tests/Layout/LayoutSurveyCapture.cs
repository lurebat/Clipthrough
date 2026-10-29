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
/// not a regression test - those are the *HeadlessTests beside it. It asserts
/// nothing and skips unless CLIPTHROUGH_LAYOUT_CAPTURE_DIR names an output
/// directory.
/// </summary>
/// <remarks>
/// Each profile installs a <see cref="TestScreens"/> screen whose working area
/// sits below a mock 31 DIP caption, so the windows' own fitting
/// (<see cref="WindowFitter"/>) runs against it as it does on a real monitor.
/// The main window is then sized as if maximized, the most room it can ever
/// have. A window cannot be laid out smaller than its minimum size, so the
/// rendered frame is what the window really lays out at; the red overlay marks
/// the part the screen cannot show.
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
            using var screens = WindowFitter.UseScreens(ScreensFor(profile));
            using var harness = MainWindowTestHarness.Create();
            SeedRealisticClips(harness);
            harness.Notifications.PublishInfo("Copied to clipboard", "The selected clip is on the clipboard and ready to paste.");
            Maximize(harness.Window, profile);
            Capture("workspace", profile, harness.Window,
            [
                ("Search box", harness.SearchBox),
                ("Type filter: Files", OnScreen.FindByContent<ToggleButton>(harness.Window, "Files")),
                ("Sort", harness.Window.FindControl<ComboBox>("SortComboBox")),
                ("Search modifiers", (Control?)OnScreen.FindByContent<CheckBox>(harness.Window, "Semantic") ?? harness.Window.FindControl<Button>("SearchOptionsButton")),
                ("Clip list", harness.ClipList),
                ("List: Delete checked", OnScreen.FindByCommand(harness.Window, harness.ViewModel.DeleteCheckedClipsCommand)),
                ("Preview: Copy", OnScreen.FindByCommand(harness.Window, harness.ViewModel.CopySelectedCommand)),
                ("Preview: Delete", OnScreen.FindByCommand(harness.Window, harness.ViewModel.DeleteSelectedCommand)),
                ("Preview: content", harness.Window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("content-stage") && b.IsEffectivelyVisible)),
                ("Toast", harness.Window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.DataContext is AppNotificationViewModel && b.CornerRadius.TopLeft >= 10 && b.IsEffectivelyVisible)),
            ]);
        }
    }

    [AvaloniaFact]
    public async Task Welcome()
    {
        SkipUnlessCapturing();
        foreach (var profile in Profiles)
        {
            using var screens = WindowFitter.UseScreens(ScreensFor(profile));
            using var harness = MainWindowTestHarness.Create();
            await harness.ViewModel.InitializeAsync();
            Dispatcher.UIThread.RunJobs();
            Maximize(harness.Window, profile);
            Capture("welcome", profile, harness.Window,
            [
                ("Database path", harness.Window.FindControl<TextBox>("WelcomeDatabasePathTextBox")),
                ("Create library", OnScreen.FindByCommand(harness.Window, harness.ViewModel.SaveSettingsCommand)),
            ]);
        }
    }

    [AvaloniaFact]
    public void PasswordPrompt()
    {
        SkipUnlessCapturing();
        foreach (var profile in Profiles)
        {
            using var screens = WindowFitter.UseScreens(ScreensFor(profile));
            using var harness = MainWindowTestHarness.Create();
            harness.ViewModel.OpenPasswordPromptForTests();
            Dispatcher.UIThread.RunJobs();
            Maximize(harness.Window, profile);
            Capture("password", profile, harness.Window,
            [
                ("Password box", harness.Window.FindControl<TextBox>("PasswordPromptTextBox")),
                ("Unlock", OnScreen.FindByCommand(harness.Window, harness.ViewModel.UnlockDatabaseCommand)),
            ]);
        }
    }

    [AvaloniaFact]
    public void Dialogs()
    {
        SkipUnlessCapturing();
        foreach (var profile in Profiles)
        {
            using var screens = WindowFitter.UseScreens(ScreensFor(profile));
            using var harness = MainWindowTestHarness.Create();
            SeedRealisticClips(harness);
            Maximize(harness.Window, profile);

            harness.ViewModel.OpenSettingsCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            var settings = harness.Window.OwnedWindows.OfType<SettingsWindow>().Single();
            CaptureOwned("settings", profile, settings);

            _ = RekeyDialog.ShowAsync(settings, harness.StorageOptions);
            Dispatcher.UIThread.RunJobs();
            var rekey = settings.OwnedWindows.OfType<RekeyDialog>().Single();
            CaptureOwned("rekey", profile, rekey);
            rekey.Close();

            _ = ConfirmDialog.ShowAsync(settings, "Move the database?", "Clipthrough will copy the library to the new folder and switch to it. This can take a while on a large library.", "Move", "Cancel");
            Dispatcher.UIThread.RunJobs();
            var confirm = settings.OwnedWindows.OfType<ConfirmDialog>().Single();
            CaptureOwned("confirm", profile, confirm);
            confirm.Close();
            settings.Close();

            foreach (var (surface, window) in new (string, Window)[]
            {
                ("about", new AboutWindow { DataContext = harness.ViewModel }),
                ("help", new HelpWindow()),
                ("logs", new SessionLogsWindow { DataContext = harness.ViewModel.SessionLogs }),
                ("aiprompt", new AiPromptWindow { DataContext = harness.ViewModel }),
            })
            {
                window.Show(harness.Window);
                Dispatcher.UIThread.RunJobs();
                CaptureOwned(surface, profile, window);
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }

    private static void SkipUnlessCapturing()
        => Assert.SkipWhen(OutputDirectory is null, "Layout survey capture: set CLIPTHROUGH_LAYOUT_CAPTURE_DIR to run.");

    /// <summary>The profile's screen, less a caption at the top for the mock title bar.</summary>
    private static TestScreens ScreensFor(Profile profile)
        => new(TestScreens.Dip(profile.WorkWidth, profile.WorkHeight - CaptionHeight, profile.Scaling, y: (int)Math.Round(CaptionHeight * profile.Scaling)));

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

    /// <summary>Sizes the main window as if maximized on <paramref name="profile"/>'s working area.</summary>
    private static void Maximize(Window window, Profile profile)
    {
        window.SetRenderScaling(profile.Scaling);
        window.Position = new PixelPoint(0, (int)Math.Round(CaptionHeight * profile.Scaling));
        window.Width = profile.WorkWidth;
        window.Height = profile.WorkHeight - CaptionHeight;
        Dispatcher.UIThread.RunJobs();
    }

    private static void CaptureOwned(string surface, Profile profile, Window dialog)
    {
        dialog.SetRenderScaling(profile.Scaling);
        Dispatcher.UIThread.RunJobs();

        // The dialog's own action buttons: those outside any scroller, which
        // the user cannot bring into view by scrolling.
        var keys = dialog.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible
                && !(b.Name ?? string.Empty).StartsWith("PART_", StringComparison.Ordinal)
                && !b.GetVisualAncestors().OfType<ScrollViewer>().Any())
            .Select(b => (LabelOf(b), (Control?)b))
            .ToList();
        Capture(surface, profile, dialog, keys);
    }

    private static void Capture(string surface, Profile profile, Window window, IEnumerable<(string Label, Control? Control)> keys)
    {
        Dispatcher.UIThread.RunJobs();
        var clientRect = new Rect(
            new Point(window.Position.X / profile.Scaling, window.Position.Y / profile.Scaling),
            window.Bounds.Size);
        var workArea = new Size(profile.WorkWidth, profile.WorkHeight);
        var frame = window.CaptureRenderedFrame();
        var results = keys.Select(k => (k.Label, Result: OnScreen.Classify(k.Control, window, clientRect.Position, workArea))).ToList();
        SaveScreenView(surface, profile, window.Title ?? surface, frame, clientRect);

        var overflowX = Math.Max(0, clientRect.Right - profile.WorkWidth) + Math.Max(0, -clientRect.X);
        var overflowY = Math.Max(0, clientRect.Bottom - profile.WorkHeight) + Math.Max(0, CaptionHeight - clientRect.Y);
        var broken = results.Where(r => r.Result.Reach != Reach.Visible)
            .Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Label}: {r.Result.Reach} ({r.Result.VisibleFraction:P0})"))
            .ToList();
        var lost = window.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Button or CheckBox or RadioButton or TextBox or ComboBox or ToggleButton && c.IsEffectivelyVisible)
            .Where(c => !(c.Name ?? string.Empty).StartsWith("PART_", StringComparison.Ordinal))
            .Select(c => (Label: LabelOf(c), Result: OnScreen.Classify(c, window, clientRect.Position, workArea)))
            .Where(r => r.Result.Reach is Reach.OffScreen or Reach.Clipped)
            .Select(r => r.Label)
            .ToList();
        AppendReport(string.Create(CultureInfo.InvariantCulture,
            $"| {surface} | {profile.Label} | {profile.WorkWidth:F0}x{profile.WorkHeight:F0} | client {clientRect.Width:F0}x{clientRect.Height:F0} at ({clientRect.X:F0},{clientRect.Y:F0}); overflow x {overflowX:F0}, y {overflowY:F0} | lost {lost.Count}: {string.Join(", ", lost.Distinct().Take(10))} | {(broken.Count > 0 ? string.Join("; ", broken) : "all key controls visible")} |"));
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

    private static string LabelOf(Control control)
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
}
