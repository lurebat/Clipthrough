using System;
using System.IO;
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
    public IObservable<ClipEntry> CapturedClips { get; } = Observable.Never<ClipEntry>();

    public void Start()
    {
    }

    public void Stop()
    {
    }
}

internal sealed class TestClipSampleDataService : IClipSampleDataService
{
    public Task SeedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class TestSystemInteractionService : ISystemInteractionService
{
    public Task CopyTextAsync(string text) => Task.CompletedTask;

    public Task CopyRichContentAsync(string richContent, string plainText, ClipContentFormat contentFormat) => Task.CompletedTask;

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
        DatabaseInitializer = new DatabaseInitializer(ConnectionFactory, SensitivityService);
        ClipStoreService = new ClipStoreService(ConnectionFactory, SensitivityService, SettingsService);
    }

    public string DatabasePath { get; }

    public TestStorageOptionsService StorageOptionsService { get; }

    public TestSettingsService SettingsService { get; }

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
