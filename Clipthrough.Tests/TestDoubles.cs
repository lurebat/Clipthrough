using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;

namespace Clipthrough.Tests;

internal sealed class TestStorageOptionsService : IStorageOptionsService
{
    private bool _hasSavedConfig;

    public TestStorageOptionsService(string databasePath)
    {
        Current = new StorageOptions
        {
            DatabasePath = databasePath,
            DatabasePassword = string.Empty,
        }.Normalize();
    }

    public StorageOptions Current { get; private set; }

    public bool HasSavedConfig => _hasSavedConfig;

    public bool DatabaseExists => File.Exists(Current.DatabasePath);

    public Task SaveAsync(StorageOptions options, CancellationToken cancellationToken = default)
    {
        Current = options.Normalize();
        _hasSavedConfig = true;
        return Task.CompletedTask;
    }

    public void SetInMemoryPassword(string password)
    {
        Current = new StorageOptions
        {
            DatabasePath = Current.DatabasePath,
            DatabasePassword = password,
        }.Normalize();
    }

    public void SetHasSavedConfig(bool value) => _hasSavedConfig = value;
}

internal sealed class TestSettingsService : ISettingsService
{
    private AppSettings? _initializeCurrent;

    public AppSettings Current { get; private set; } = AppSettings.Default;

    public int SaveCallCount { get; private set; }

    public bool HasSavedSettings { get; private set; }

    public event EventHandler<AppSettings>? SettingsChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initializeCurrent is not null)
        {
            Current = _initializeCurrent;
            SettingsChanged?.Invoke(this, Current);
        }

        return Task.CompletedTask;
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings.Normalize();
        HasSavedSettings = true;
        SaveCallCount++;
        SettingsChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public void SetCurrent(AppSettings settings)
    {
        Current = settings.Normalize();
        SettingsChanged?.Invoke(this, Current);
    }

    public void SetCurrentOnInitialize(AppSettings settings)
    {
        _initializeCurrent = settings.Normalize();
        HasSavedSettings = true;
        Current = AppSettings.Default;
    }

    public void SetHasSavedSettings(bool value) => HasSavedSettings = value;
}

internal sealed class TestClipboardMonitorService : IClipboardMonitorService
{
    private readonly Subject<ClipEntry> _capturedClips = new();

    public IObservable<ClipEntry> CapturedClips => _capturedClips.AsObservable();

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void SuppressNext()
    {
    }

    public void Emit(ClipEntry clip) => _capturedClips.OnNext(clip);
}

internal sealed class TestClipSampleDataService : IClipSampleDataService
{
    public Task SeedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class TestNotificationService : IAppNotificationService
{
    private readonly Subject<AppNotification> _notifications = new();

    public IObservable<AppNotification> Notifications => _notifications.AsObservable();

    public AppNotification? LastNotification { get; private set; }

    public void Publish(AppNotification notification)
    {
        LastNotification = notification;
        _notifications.OnNext(notification);
    }

    public void PublishInfo(string title, string message) => Publish(new AppNotification
    {
        Title = title,
        Message = message,
        Level = AppNotificationLevel.Information,
    });

    public void PublishWarning(string title, string message) => Publish(new AppNotification
    {
        Title = title,
        Message = message,
        Level = AppNotificationLevel.Warning,
    });

    public void PublishError(string title, string message) => Publish(new AppNotification
    {
        Title = title,
        Message = message,
        Level = AppNotificationLevel.Error,
    });
}

internal sealed class TestClipExportService : IClipExportService
{
    public string? LastExportDirectory { get; private set; }

    public string? LastPrimaryPath { get; private set; }

    public Task<ClipExportResult> ExportAsync(ClipEntry clip, CancellationToken cancellationToken = default)
    {
        LastExportDirectory = Path.Combine(Path.GetTempPath(), $"clip-export-{clip.Id}");
        LastPrimaryPath = Path.Combine(LastExportDirectory, "rendered.txt");
        return Task.FromResult(new ClipExportResult(LastExportDirectory, LastPrimaryPath));
    }
}

internal sealed class TestImageEditorService : IImageEditorService
{
    public byte[]? LastEditedInputBytes { get; private set; }

    public string? LastImageFilePath { get; private set; }

    public byte[]? ResultBytes { get; set; }

    public Task<byte[]?> EditImageAsync(byte[] imageBytes, string? imageFilePath = null, CancellationToken cancellationToken = default)
    {
        LastEditedInputBytes = imageBytes;
        LastImageFilePath = imageFilePath;
        return Task.FromResult(ResultBytes);
    }
}

internal sealed class TestSessionLogService : ISessionLogService
{
    private readonly Subject<SessionLogEntry> _entries = new();
    private readonly List<SessionLogEntry> _snapshot = [];

    public IObservable<SessionLogEntry> Entries => _entries.AsObservable();

    public IReadOnlyList<SessionLogEntry> Snapshot() => _snapshot.ToArray();

    public void Emit(SessionLogEntry entry)
    {
        _snapshot.Insert(0, entry);
        _entries.OnNext(entry);
    }
}

internal sealed class TestSystemInteractionService : ISystemInteractionService
{
    public string? LastCopiedText { get; private set; }

    public string? LastCopiedRichContent { get; private set; }

    public string? LastCopiedRichPlainText { get; private set; }

    public ClipContentFormat? LastCopiedRichContentFormat { get; private set; }

    public string? LastOpenedPath { get; private set; }

    public AppNotification? LastSystemNotification { get; private set; }

    public int BitmapCopyCount { get; private set; }

    public int SimulatedPasteCount { get; private set; }

    public Task CopyTextAsync(string text)
    {
        LastCopiedText = text;
        return Task.CompletedTask;
    }

    public Task CopyRichContentAsync(string richContent, string plainText, ClipContentFormat contentFormat)
    {
        LastCopiedRichContent = richContent;
        LastCopiedRichPlainText = plainText;
        LastCopiedRichContentFormat = contentFormat;
        return Task.CompletedTask;
    }

    public Task CopyBitmapAsync(Bitmap bitmap)
    {
        BitmapCopyCount++;
        return Task.CompletedTask;
    }

    public Task OpenPathAsync(string path)
    {
        LastOpenedPath = path;
        return Task.CompletedTask;
    }

    public Task OpenUrlAsync(string url)
    {
        LastOpenedPath = url;
        return Task.CompletedTask;
    }

    public Task OpenContainingDirectoryAsync(string path) => Task.CompletedTask;

    public Task OpenInEditorAsync(string filePath, string editorPath)
    {
        LastOpenedPath = filePath;
        return Task.CompletedTask;
    }

    public Task OpenInDiffToolAsync(string leftPath, string rightPath, string diffToolPath) => Task.CompletedTask;

    public void SimulatePasteKeystroke()
    {
        SimulatedPasteCount++;
    }

    public void ShowNotification(AppNotification notification)
    {
        LastSystemNotification = notification;
    }

    public bool TryRegisterGlobalHotKey(Window window, HotkeyGesture hotkey, Action callback) => true;

    public bool TryRegisterGlobalHotKey(Window window, string name, HotkeyGesture hotkey, Action callback) => true;

    public void UnregisterGlobalHotKey()
    {
    }

    public void UnregisterGlobalHotKey(string name)
    {
    }

    public void UnregisterAllGlobalHotKeys()
    {
    }

    public PixelPoint? GetCaretScreenPosition() => null;

    public bool IsTargetWindowElevated() => false;

    public void SyncStartWithWindows(bool enabled)
    {
    }
}

internal sealed class TestSearchHistoryService : ISearchHistoryService
{
    private readonly List<string> _history = [];

    public Task SaveSearchAsync(string query, CancellationToken cancellationToken = default)
    {
        _history.Remove(query);
        _history.Insert(0, query);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetRecentSearchesAsync(int limit = 20, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(_history.GetRange(0, Math.Min(limit, _history.Count)));

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _history.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class TemporaryDatabaseScope : IDisposable
{
    private readonly string _directoryPath;

    public TemporaryDatabaseScope()
    {
        _directoryPath = Path.Combine(Path.GetTempPath(), "Clipthrough.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directoryPath);
        DatabasePath = Path.Combine(_directoryPath, "clipthrough.db");

        StorageOptionsService = new TestStorageOptionsService(DatabasePath);
        ConnectionFactory = new SqliteConnectionFactory(StorageOptionsService);
        SensitivityService = new SensitivityService(ConnectionFactory);
        SettingsService = new TestSettingsService();
        NotificationService = new TestNotificationService();
        ClipExportService = new TestClipExportService();
        SearchHistoryService = new SearchHistoryService(ConnectionFactory);
        DatabaseInitializer = new DatabaseInitializer(ConnectionFactory, SensitivityService);
        ClipStoreService = new ClipStoreService(ConnectionFactory, SensitivityService, SettingsService, NotificationService);
    }

    public string DatabasePath { get; }

    public TestStorageOptionsService StorageOptionsService { get; }

    public TestSettingsService SettingsService { get; }

    public TestNotificationService NotificationService { get; }

    public TestClipExportService ClipExportService { get; }

    public SearchHistoryService SearchHistoryService { get; }

    public SqliteConnectionFactory ConnectionFactory { get; }

    public SensitivityService SensitivityService { get; }

    public DatabaseInitializer DatabaseInitializer { get; }

    public ClipStoreService ClipStoreService { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directoryPath))
            {
                Directory.Delete(_directoryPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class TestAiTransformService : IAiTransformService
{
    public bool IsConfigured => false;
    public Task<string> TransformAsync(string systemPrompt, string input, CancellationToken cancellationToken = default)
        => Task.FromResult(input);
    public Task<string> DescribeImageAsync(string systemPrompt, byte[] imageBytes, string mediaType, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);
    public Task<byte[]> EditImageAsync(string prompt, byte[] imageBytes, string mediaType, CancellationToken cancellationToken = default)
        => Task.FromResult(imageBytes);
}

internal sealed class TestOcrService : IOcrService
{
    public bool IsAvailable => false;
    public Task<OcrResult> ExtractTextAsync(byte[] imageBytes, string languages, CancellationToken cancellationToken = default)
        => Task.FromResult(new OcrResult(false, string.Empty, "stub"));
}
