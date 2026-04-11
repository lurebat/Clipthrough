using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;

namespace Clipthrough.Tests;

internal sealed class TestStorageOptionsService : IStorageOptionsService
{
    public TestStorageOptionsService(string databasePath)
    {
        Current = new StorageOptions
        {
            DatabasePath = databasePath,
            DatabasePassword = string.Empty,
        }.Normalize();
    }

    public StorageOptions Current { get; private set; }

    public Task SaveAsync(StorageOptions options, CancellationToken cancellationToken = default)
    {
        Current = options.Normalize();
        return Task.CompletedTask;
    }
}

internal sealed class TestSettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = AppSettings.Default;

    public event EventHandler<AppSettings>? SettingsChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings.Normalize();
        SettingsChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public void SetCurrent(AppSettings settings)
    {
        Current = settings.Normalize();
        SettingsChanged?.Invoke(this, Current);
    }
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

    public Task CopyBitmapAsync(Bitmap bitmap) => Task.CompletedTask;

    public Task OpenPathAsync(string path) => Task.CompletedTask;

    public Task OpenContainingDirectoryAsync(string path) => Task.CompletedTask;

    public bool TryRegisterGlobalHotKey(Window window, HotkeyGesture hotkey, Action callback) => true;

    public void UnregisterGlobalHotKey()
    {
    }

    public void SyncStartWithWindows(bool enabled)
    {
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
        SensitivityService = new SensitivityService();
        SettingsService = new TestSettingsService();
        NotificationService = new TestNotificationService();
        DatabaseInitializer = new DatabaseInitializer(ConnectionFactory, SensitivityService);
        ClipStoreService = new ClipStoreService(ConnectionFactory, SensitivityService, SettingsService, NotificationService);
    }

    public string DatabasePath { get; }

    public TestStorageOptionsService StorageOptionsService { get; }

    public TestSettingsService SettingsService { get; }

    public TestNotificationService NotificationService { get; }

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
