using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// A real <see cref="MainWindow"/> on a throwaway database, for tests that need
/// keystrokes to travel the actual routed-event path rather than calling
/// handlers directly.
/// </summary>
internal sealed class MainWindowTestHarness : IDisposable
{
    private readonly TemporaryDatabaseScope _scope;

    private MainWindowTestHarness(TemporaryDatabaseScope scope, MainWindowViewModel viewModel, MainWindow window)
    {
        _scope = scope;
        ViewModel = viewModel;
        Window = window;
    }

    public MainWindowViewModel ViewModel { get; }

    public MainWindow Window { get; }

    public TextBox SearchBox => Window.FindControl<TextBox>("SearchTextBox")!;

    public ListBox ClipList => Window.FindControl<ListBox>("ClipsListBox")!;

    public static MainWindowTestHarness Create()
    {
        var scope = new TemporaryDatabaseScope();

        // Task.Run so the awaited continuations land on the thread pool rather
        // than being posted back to the (currently blocked) UI dispatcher, and
        // so every Avalonia object below is constructed on the UI thread. An
        // `await` here would let the rest of this method resume on a pool
        // thread, and `new MainWindow()` would then throw a thread-affinity
        // error during teardown -- intermittently, depending on scheduling.
        Task.Run(() => scope.DatabaseInitializer.InitializeAsync()).GetAwaiter().GetResult();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var viewModel = new MainWindowViewModel(
            scope.ClipStoreService,
            new TestClipboardMonitorService(),
            new TestClipSampleDataService(),
            scope.SettingsService,
            new TestSystemInteractionService(),
            scope.StorageOptionsService,
            scope.SensitivityService,
            scope.NotificationService,
            new TestSessionLogService(),
            scope.ClipExportService,
            new TestImageEditorService(),
            scope.SearchHistoryService,
            new TestAiTransformService(),
            new TestOcrService(),
            new NoOpBackgroundOcrQueue(),
            new BackgroundJobIndicator(),
            scope.DatabaseInitializer);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return new MainWindowTestHarness(scope, viewModel, window);
    }

    /// <summary>
    /// Appends <paramref name="count"/> plain-text clips to the list, newest first,
    /// so focus and navigation behave as they do with real history.
    /// </summary>
    public void SeedClips(int count)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            var entry = new ClipEntry
            {
                Id = i + 1,
                Content = $"clip-{i + 1}",
                ContentBytes = System.Text.Encoding.UTF8.GetBytes($"clip-{i + 1}"),
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                SourceApp = "Tests",
                Hash = $"hash-{i + 1}",
                LastCopiedAt = now.AddSeconds(-i),
                FirstCopiedAt = now.AddSeconds(-i),
            };
            ViewModel.Clips.Add(new ClipItemViewModel(entry));
        }

        Dispatcher.UIThread.RunJobs();
    }

    public void FocusSearchBox()
    {
        SearchBox.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.True(SearchBox.IsKeyboardFocusWithin, "Search box did not take focus; the test would not exercise its key path.");
    }

    public void FocusClipList()
    {
        var list = ClipList;
        // An empty ListBox refuses focus, which would leave the search box
        // focused and make every assertion downstream vacuous.
        list.Focusable = true;
        list.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.False(SearchBox.IsKeyboardFocusWithin, "Clip list did not take focus; the redirect is not under test.");
    }

    public void Dispose()
    {
        // Everything is torn down before the queue is drained, because closing a
        // window and disposing the view model both post their own jobs. Draining
        // first leaves those queued for the runner's own RunJobs(), which
        // Avalonia calls after xUnit has already retired the test context -- and
        // anything touching xUnit from there fails the test in cleanup rather
        // than in an assertion.
        try { Window.Close(); } catch { /* test teardown */ }
        ViewModel.Dispose();
        _scope.Dispose();

        // Posting from inside a posted job is normal, so one pass can leave work
        // behind; a handful of passes settles it.
        for (var i = 0; i < 5; i++)
        {
            try { Dispatcher.UIThread.RunJobs(); } catch { /* test teardown */ }
        }
    }

    private sealed class NoOpBackgroundOcrQueue : IBackgroundOcrQueue
    {
        public IObservable<long> OcrCompleted { get; } = System.Reactive.Linq.Observable.Empty<long>();
        public IObservable<System.Reactive.Unit> QueueChanged { get; } = System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();
        public bool IsRunning { get; private set; }
        public void Start() { IsRunning = true; }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public void Enqueue(long clipId) { }
        public Task EnqueueBacklogAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
