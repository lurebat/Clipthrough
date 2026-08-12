using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Clipthrough.Database;
using Clipthrough.Controls;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using Clipthrough.Views;
using System.Reactive.Threading.Tasks;
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

    [AvaloniaFact]
    public void RichWebContentView_LoadsInsideHeadlessWindow()
    {
        var view = new RichWebContentView
        {
            ContentFormat = ClipContentFormat.Html,
            Markup = "<p>Hello <strong>headless</strong> world</p>",
        };
        var window = new Window
        {
            Content = view,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view.Content);
    }

    [AvaloniaFact]
    public void RichWebContentView_ConvertsRtfBeforeRendering()
    {
        var rtf = @"{\rtf1\ansi{\colortbl ;\red255\green0\blue0;}\cf1 hello}";

        // Verify the RTF-to-HTML conversion produces output with the text
        var html = Clipthrough.Presentation.RtfToHtmlConverter.Convert(rtf);
        Assert.Contains("hello", html);

        var view = new RichWebContentView
        {
            ContentFormat = ClipContentFormat.Rtf,
            Markup = rtf,
        };
        var window = new Window
        {
            Content = view,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view.Content);
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
        public void Start() { }
        public Task StopAsync() => Task.CompletedTask;
        public void Enqueue(long clipId) { }
        public Task EnqueueBacklogAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

}
