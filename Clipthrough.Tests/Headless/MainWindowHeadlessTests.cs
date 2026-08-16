using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.Database;
using Clipthrough.Controls;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using Clipthrough.Views;
using System.Reactive.Threading.Tasks;
using Vellum;
using Vellum.Avalonia;
using Xunit;

namespace Clipthrough.Tests.Headless;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public void MainWindow_LoadsExpectedControls()
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(window.FindControl<TextBox>("SearchTextBox"));
        Assert.NotNull(window.FindControl<ListBox>("ClipsListBox"));
        Assert.NotNull(window.FindControl<EmbeddedImageEditorView>("SelectedImageEditor"));
    }

    [AvaloniaFact]
    public void SearchTextBox_AcceptsHeadlessTextInput()
    {
        var window = new MainWindow();

        window.Show();
        var searchTextBox = window.FindControl<TextBox>("SearchTextBox");
        Assert.NotNull(searchTextBox);

        searchTextBox!.Focus();
        window.KeyTextInput("invoice");

        Assert.Equal("invoice", searchTextBox.Text);
    }

    [AvaloniaFact]
    public void MainWindow_LoadsWelcomeSetupControls()
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(window.FindControl<TextBox>("WelcomeDatabasePathTextBox"));
        Assert.NotNull(window.FindControl<Button>("WelcomeDatabasePathBrowseButton"));
        Assert.NotNull(window.FindControl<TextBox>("WelcomeDatabasePasswordTextBox"));
    }

    /// <summary>
    /// The rich preview renders natively now. These assert the text actually reaches a
    /// document, because "the control loaded" passed just as happily against the WebView
    /// that showed nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task RichDocumentView_RendersHtmlIntoADocument()
    {
        var view = new RichDocumentView
        {
            ContentFormat = ClipContentFormat.Html,
            Markup = "<p>Hello <strong>headless</strong> world</p>",
        };
        var window = new Window { Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await view.PendingRender;

        var text = DocumentText.Of(view.Viewer.Document);
        Assert.Contains("Hello", text, StringComparison.Ordinal);
        Assert.Contains("headless", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task RichDocumentView_RendersRtfIntoADocument()
    {
        var view = new RichDocumentView
        {
            ContentFormat = ClipContentFormat.Rtf,
            Markup = @"{\rtf1\ansi{\colortbl ;\red255\green0\blue0;}\cf1 hello}",
        };
        var window = new Window { Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await view.PendingRender;

        Assert.Contains("hello", DocumentText.Of(view.Viewer.Document), StringComparison.Ordinal);
    }

    /// <summary>
    /// Oversized payloads degrade to bounded text rather than blanking the preview.
    ///
    /// Deliberately no Window: the contract is what the control puts in the fallback, and
    /// showing a window drags in text layout, which is the very thing being guarded
    /// against. An earlier version of this test did show one and took over four minutes
    /// on a 600 KB single-word payload - the bug it now pins.
    /// </summary>
    [AvaloniaFact]
    public async Task RichDocumentView_WithAnOversizedPayload_FallsBackToBoundedText()
    {
        // The marker goes first: the fallback is deliberately bounded, so a marker at the
        // end would be truncated away and the test would be asserting the wrong thing.
        var huge = "<p>needle " + new string('x', 600 * 1024) + "</p>";
        var view = new RichDocumentView
        {
            ContentFormat = ClipContentFormat.Html,
            Markup = huge,
        };

        await view.PendingRender;

        Assert.True(view.FallbackText.IsVisible, "an oversized payload should degrade to text, not blank the preview");
        Assert.False(view.Viewer.IsVisible);
        Assert.Contains("needle", view.FallbackText.Text ?? string.Empty, StringComparison.Ordinal);

        // The bound is the point. Handing an unbounded string to a wrapping TextBlock costs
        // minutes of shaping on the UI thread, and the payload here is one 600,000-character
        // word, which is the worst case for finding break opportunities.
        Assert.True(
            (view.FallbackText.Text?.Length ?? 0) < 32 * 1024,
            $"fallback text was {view.FallbackText.Text?.Length ?? 0} chars; it must be bounded");
    }

    /// <summary>
    /// A clip that is one enormous run with no break opportunity - a copied base64 blob, a
    /// minified script, a long URL - must not be rendered as a document.
    ///
    /// Avalonia's line breaking is quadratic in the length of such a run, because the cost
    /// is characters x lines: measured upstream at width 400, 20,000 chars took 217 ms,
    /// 40,000 took 801 ms and 80,000 took 3,047 ms, while the same 80,000 characters as
    /// ordinary words took 97 ms. A few hundred thousand characters is therefore minutes of
    /// frozen UI, and the size cap does not catch it because such a clip is well under it.
    /// </summary>
    [AvaloniaFact]
    public async Task RichDocumentView_WithOneEnormousUnbreakableRun_DoesNotBuildADocument()
    {
        // Comfortably under the size cap, so only the unbreakable-run guard can catch it.
        var base64ish = new string('A', 120 * 1024);
        var view = new RichDocumentView
        {
            ContentFormat = ClipContentFormat.Html,
            Markup = "<p>needle " + base64ish + "</p>",
        };

        await view.PendingRender;

        Assert.True(view.FallbackText.IsVisible, "an unbreakable run must take the bounded text path");
        Assert.False(view.Viewer.IsVisible);
        Assert.True(
            (view.FallbackText.Text?.Length ?? 0) < 32 * 1024,
            $"fallback text was {view.FallbackText.Text?.Length ?? 0} chars; it must be bounded");

        // Anti-vacuity: the same payload broken into ordinary words is well within budget
        // and must still render as a document, so this is pinning the *unbreakable* case
        // rather than just "large content falls back".
        var words = string.Join(' ', Enumerable.Repeat("AAAAAAAA", (120 * 1024) / 9));
        var breakable = new RichDocumentView
        {
            ContentFormat = ClipContentFormat.Html,
            Markup = "<p>needle " + words + "</p>",
        };

        await breakable.PendingRender;

        Assert.True(breakable.Viewer.IsVisible, "breakable content of the same size should still render as a document");
        Assert.False(breakable.FallbackText.IsVisible);
    }

    [AvaloniaFact]
    public async Task RestoreOwnedWindowsForCurrentState_ReshowsHiddenSettingsWindow()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);
        await scope.DatabaseInitializer.InitializeAsync();

        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = new MainWindowViewModel(
            scope.ClipStoreService,
            clipboardMonitor,
            new TestClipSampleDataService(),
            scope.SettingsService,
            systemInteraction,
            scope.StorageOptionsService,
            scope.SensitivityService,
            scope.NotificationService,
            sessionLogService,
            scope.ClipExportService,
            new TestImageEditorService(),
            scope.SearchHistoryService,
            new TestAiTransformService(),
            new TestOcrService(),
            new NoOpBackgroundOcrQueue(),
            new BackgroundJobIndicator(),
            scope.DatabaseInitializer);

        await viewModel.InitializeAsync();

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await viewModel.OpenSettingsCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        var settingsWindow = GetOwnedSettingsWindow(window);
        Assert.NotNull(settingsWindow);
        Assert.True(settingsWindow!.IsVisible);

        window.Hide();
        settingsWindow.Hide();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        InvokeRestoreOwnedWindows(window);
        Dispatcher.UIThread.RunJobs();

        Assert.True(settingsWindow.IsVisible);

        settingsWindow.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task OpenAiPrompt_ShowsOwnedAiPromptWindow()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);
        await scope.DatabaseInitializer.InitializeAsync();

        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = new MainWindowViewModel(
            scope.ClipStoreService,
            clipboardMonitor,
            new TestClipSampleDataService(),
            scope.SettingsService,
            systemInteraction,
            scope.StorageOptionsService,
            scope.SensitivityService,
            scope.NotificationService,
            sessionLogService,
            scope.ClipExportService,
            new TestImageEditorService(),
            scope.SearchHistoryService,
            new TestAiTransformService(isConfigured: true),
            new TestOcrService(),
            new NoOpBackgroundOcrQueue(),
            new BackgroundJobIndicator(),
            scope.DatabaseInitializer);

        await viewModel.InitializeAsync();

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await viewModel.OpenAiPromptCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        var aiPromptWindow = GetOwnedAiPromptWindow(window);
        Assert.NotNull(aiPromptWindow);
        Assert.True(aiPromptWindow!.IsVisible);

        aiPromptWindow.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void AiPromptWindow_CtrlEnterInsertsNewLine()
    {
        var window = new AiPromptWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var textBox = window.FindControl<TextBox>("AiPromptInputTextBox");
        Assert.NotNull(textBox);
        textBox!.Text = "ab";
        textBox.CaretIndex = 1;
        textBox.SelectionStart = 1;
        textBox.SelectionEnd = 1;

        textBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            KeyModifiers = KeyModifiers.Control,
            Source = textBox,
        });

        Assert.Equal($"a{Environment.NewLine}b", textBox.Text);
        Assert.Equal(1 + Environment.NewLine.Length, textBox.CaretIndex);

        window.Close();
    }

    [AvaloniaFact]
    public async Task AiPromptWindow_EnterSubmitsPrompt()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);
        await scope.DatabaseInitializer.InitializeAsync();

        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = new MainWindowViewModel(
            scope.ClipStoreService,
            clipboardMonitor,
            new TestClipSampleDataService(),
            scope.SettingsService,
            systemInteraction,
            scope.StorageOptionsService,
            scope.SensitivityService,
            scope.NotificationService,
            sessionLogService,
            scope.ClipExportService,
            new TestImageEditorService(),
            scope.SearchHistoryService,
            new TestAiTransformService(isConfigured: true),
            new TestOcrService(),
            new NoOpBackgroundOcrQueue(),
            new BackgroundJobIndicator(),
            scope.DatabaseInitializer);

        await viewModel.InitializeAsync();
        viewModel.OpenAiPromptCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();

        var window = new AiPromptWindow
        {
            DataContext = viewModel,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var textBox = window.FindControl<TextBox>("AiPromptInputTextBox");
        Assert.NotNull(textBox);
        textBox!.Text = "what's in there";
        textBox.CaretIndex = textBox.Text.Length;
        viewModel.AiPromptInput = textBox.Text;

        textBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            KeyModifiers = KeyModifiers.None,
            Source = textBox,
        });

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Select one or more text or file clips first.", viewModel.AiPromptError);

        window.Close();
    }

    [AvaloniaFact]
    public async Task CopyShortcut_KeepsWindowVisible()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);
        await scope.DatabaseInitializer.InitializeAsync();

        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = new MainWindowViewModel(
            scope.ClipStoreService,
            clipboardMonitor,
            new TestClipSampleDataService(),
            scope.SettingsService,
            systemInteraction,
            scope.StorageOptionsService,
            scope.SensitivityService,
            scope.NotificationService,
            sessionLogService,
            scope.ClipExportService,
            new TestImageEditorService(),
            scope.SearchHistoryService,
            new TestAiTransformService(),
            new TestOcrService(),
            new NoOpBackgroundOcrQueue(),
            new BackgroundJobIndicator(),
            scope.DatabaseInitializer);

        await viewModel.InitializeAsync();

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "copy me",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("copy me"),
            SourceApp = "Tests",
        });

        Assert.NotNull(clip);

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        viewModel.SelectedClip = new ClipItemViewModel(clip!);

        InvokeCopySelectedWithoutClosing(viewModel);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.Equal("copy me", systemInteraction.LastCopiedText);
        Assert.Equal(0, systemInteraction.SimulatedPasteCount);

        window.Close();
    }

    [AvaloniaFact]
    public async Task PasteAction_HidesWindowAndSimulatesPaste()
    {
        using var scope = new TemporaryDatabaseScope();
        scope.SettingsService.SetHasSavedSettings(true);
        scope.StorageOptionsService.SetHasSavedConfig(true);
        await scope.DatabaseInitializer.InitializeAsync();

        var clipboardMonitor = new TestClipboardMonitorService();
        var systemInteraction = new TestSystemInteractionService();
        var sessionLogService = new TestSessionLogService();
        using var viewModel = new MainWindowViewModel(
            scope.ClipStoreService,
            clipboardMonitor,
            new TestClipSampleDataService(),
            scope.SettingsService,
            systemInteraction,
            scope.StorageOptionsService,
            scope.SensitivityService,
            scope.NotificationService,
            sessionLogService,
            scope.ClipExportService,
            new TestImageEditorService(),
            scope.SearchHistoryService,
            new TestAiTransformService(),
            new TestOcrService(),
            new NoOpBackgroundOcrQueue(),
            new BackgroundJobIndicator(),
            scope.DatabaseInitializer);

        await viewModel.InitializeAsync();

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "paste me",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("paste me"),
            SourceApp = "Tests",
        });

        Assert.NotNull(clip);

        // The window must be handed the service it is expected to drive: the
        // parameterless ctor chains to this(null), and ExecutePasteSelectedAndHide
        // calls it null-conditionally, so both the foreground restore and the
        // paste keystroke would silently no-op.
        var window = new MainWindow(systemInteraction)
        {
            DataContext = viewModel,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        viewModel.SelectedClip = new ClipItemViewModel(clip!);

        InvokePasteSelectedAndHide(window, viewModel);

        // ExecutePasteSelectedAndHide is async void and delays 150ms before
        // simulating the keystroke - and that delay only starts once the copy
        // has completed. A fixed wait here races it, so poll instead.
        await WaitForUiAsync(() => systemInteraction.SimulatedPasteCount == 1);

        Assert.False(window.IsVisible);
        Assert.Equal("paste me", systemInteraction.LastCopiedText);
        Assert.Equal(1, systemInteraction.SimulatedPasteCount);
    }

    /// <summary>
    /// Pumps the Avalonia dispatcher until <paramref name="condition"/> holds or
    /// the timeout expires. Returns either way so the caller's assertion is what
    /// reports the failure.
    /// </summary>
    private static async Task WaitForUiAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static SettingsWindow? GetOwnedSettingsWindow(MainWindow window)
        => (SettingsWindow?)typeof(MainWindow)
            .GetField("m_settingsWindow", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(window);

    private static AiPromptWindow? GetOwnedAiPromptWindow(MainWindow window)
        => (AiPromptWindow?)typeof(MainWindow)
            .GetField("m_aiPromptWindow", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(window);

    private static void InvokeCopySelectedWithoutClosing(MainWindowViewModel viewModel)
        => typeof(MainWindow)
            .GetMethod("ExecuteCopySelectedWithoutClosing", BindingFlags.Static | BindingFlags.NonPublic)?
            .Invoke(null, [viewModel]);

    private static void InvokePasteSelectedAndHide(MainWindow window, MainWindowViewModel viewModel)
        => typeof(MainWindow)
            .GetMethod("ExecutePasteSelectedAndHide", BindingFlags.Instance | BindingFlags.NonPublic)?
            .Invoke(window, [viewModel]);

    private static void InvokeRestoreOwnedWindows(MainWindow window)
        => typeof(MainWindow)
            .GetMethod("RestoreOwnedWindowsForCurrentState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?
            .Invoke(window, null);

    private sealed class NoOpBackgroundOcrQueue : IBackgroundOcrQueue
    {
        public IObservable<long> OcrCompleted { get; } = System.Reactive.Linq.Observable.Empty<long>();
        public IObservable<System.Reactive.Unit> QueueChanged { get; } = System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();
        public bool IsRunning { get; private set; }
        public void Start() { IsRunning = true; }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public void Enqueue(long clipId) { }
        public Task EnqueueBacklogAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

}
