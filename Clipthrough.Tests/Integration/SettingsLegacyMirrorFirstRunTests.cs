using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Every settings save also mirrors the settings into the database, as a
/// fallback for an unreadable settings.json. On a fresh install the first save
/// happens before the library exists - Create library saves settings, then
/// creates the database - so the mirror tried to open a database in a folder
/// that did not exist yet and logged "Legacy settings mirror save failed:
/// SQLite Error 14" into the user-visible Logs on every first run. (audit F-51)
/// </summary>
public sealed class SettingsLegacyMirrorFirstRunTests : IDisposable
{
    private const string MirrorKey = "settings:app:v1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clipthrough.Tests", "mirror-" + Guid.NewGuid().ToString("N"));

    public SettingsLegacyMirrorFirstRunTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task SavingBeforeTheLibraryFolderExists_LogsNothingAndCreatesNothing()
    {
        // The real first run: the database lives in a folder nobody has created.
        var databasePath = Path.Combine(_root, "Data", "clipthrough.db");
        var service = NewSettingsService(databasePath, out _);

        var warnings = await CaptureMirrorTracesAsync(() => service.SaveAsync(AppSettings.Default with { MaxClipSizeBytes = 4321 }));

        Assert.Empty(warnings);
        Assert.False(File.Exists(databasePath), "the mirror created a database file the welcome screen had not asked for");
    }

    /// <summary>
    /// The same first save when the folder already exists. Opening read-write
    /// with create would make an empty database file here and then fail on the
    /// missing table instead - a different error, and a stray file that makes
    /// the library look as if it already exists.
    /// </summary>
    [Fact]
    public async Task SavingBeforeTheDatabaseFileExists_LogsNothingAndCreatesNothing()
    {
        var databasePath = Path.Combine(_root, "clipthrough.db");
        var service = NewSettingsService(databasePath, out _);

        var warnings = await CaptureMirrorTracesAsync(() => service.SaveAsync(AppSettings.Default with { MaxClipSizeBytes = 4321 }));

        Assert.Empty(warnings);
        Assert.False(File.Exists(databasePath), "the mirror created an empty database file");
    }

    [Fact]
    public async Task OnceTheDatabaseExists_TheMirrorIsWritten()
    {
        var databasePath = Path.Combine(_root, "Data", "clipthrough.db");
        var service = NewSettingsService(databasePath, out var factory);
        await service.SaveAsync(AppSettings.Default with { MaxClipSizeBytes = 4321 });

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await new DatabaseInitializer(factory, new SensitivityService(factory)).InitializeAsync();
        Assert.Null(await ReadMirrorAsync(factory));

        var warnings = await CaptureMirrorTracesAsync(() => service.SaveAsync(AppSettings.Default with { MaxClipSizeBytes = 8765 }));

        Assert.Empty(warnings);
        var mirrored = await ReadMirrorAsync(factory);
        Assert.NotNull(mirrored);
        Assert.Contains("8765", mirrored, StringComparison.Ordinal);
    }

    private SettingsService NewSettingsService(string databasePath, out SqliteConnectionFactory factory)
    {
        factory = new SqliteConnectionFactory(new TestStorageOptionsService(databasePath));
        return new SettingsService(factory, new PassThroughDataProtection(), Path.Combine(_root, "settings", "settings.json"));
    }

    private static async Task<string?> ReadMirrorAsync(SqliteConnectionFactory factory)
    {
        await using var connection = factory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", MirrorKey);
        return await command.ExecuteScalarAsync() as string;
    }

    /// <summary>
    /// Trace listeners are process-wide and other tests run in parallel, so only
    /// the mirror's own messages are kept.
    /// </summary>
    private static async Task<string[]> CaptureMirrorTracesAsync(Func<Task> action)
    {
        var sink = new ConcurrentQueue<string>();
        using var listener = new CaptureListener(sink);
        Trace.Listeners.Add(listener);
        try
        {
            await action();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        return sink.Where(message => message.Contains("Legacy settings mirror", StringComparison.Ordinal)).ToArray();
    }

    private sealed class CaptureListener(ConcurrentQueue<string> sink) : TraceListener
    {
        public override void Write(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }

        public override void WriteLine(string? message) => Write(message);
    }

    private sealed class PassThroughDataProtection : IDataProtectionService
    {
        public bool CanPersistSecrets => true;
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }
}
