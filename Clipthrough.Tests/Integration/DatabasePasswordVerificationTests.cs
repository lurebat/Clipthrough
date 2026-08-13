using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// SQLCipher derives the key when the connection opens. Measured on this machine
/// at ~530ms, four times in a row with the connection pool cleared between - and
/// it does not get cheaper with a smaller database, because it is key stretching
/// rather than I/O.
///
/// Two startup paths used to pay that on the UI thread: the "Remember password"
/// auto-unlock, and the unlock prompt itself. The second one looked safe because
/// it awaited SqliteConnection.OpenAsync, which does not help - measured at 530ms
/// before the await, handing back an already-completed task.
/// </summary>
public sealed class DatabasePasswordVerificationTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const int SqliteNotADatabase = 26;
    private const int SqliteCantOpen = 14;

    private readonly string _directory;
    private readonly string _databasePath;
    private readonly string _configPath;

    public DatabasePasswordVerificationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "clipthrough-pw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "clips.db");
        _configPath = Path.Combine(_directory, "storage.json");
    }

    [Fact]
    public async Task TryOpenWithPasswordAsync_DoesNotDeriveTheKeyOnTheCallingThread()
    {
        await CreateEncryptedDatabaseAsync();
        var service = CreateService();
        SqliteConnection.ClearAllPools();

        var pending = service.TryOpenWithPasswordAsync(Password);

        // Half a second of key stretching cannot have finished in the time it took
        // to get to this line, so a completed task here means it ran inline.
        Assert.False(pending.IsCompleted, "the key derivation ran on the calling thread");
        Assert.Null(await pending);
    }

    [Fact]
    public async Task TryOpenWithPasswordAsync_WrongPassword_ReportsItAsAWrongPassword()
    {
        await CreateEncryptedDatabaseAsync();
        var service = CreateService();

        var failure = await service.TryOpenWithPasswordAsync("not the password");

        Assert.NotNull(failure);
        Assert.Equal(SqliteNotADatabase, failure.SqliteErrorCode);
        Assert.True(SqliteErrors.IsPasswordFailure(failure));
    }

    [Fact]
    public async Task TryOpenWithPasswordAsync_MissingFile_IsNotReportedAsAWrongPassword()
    {
        var service = CreateService();

        var failure = await service.TryOpenWithPasswordAsync(Password);

        Assert.NotNull(failure);
        Assert.Equal(SqliteCantOpen, failure.SqliteErrorCode);
        Assert.False(SqliteErrors.IsPasswordFailure(failure));
    }

    /// <summary>
    /// A missing file used to make CanOpenWithPassword return false, and that is
    /// what its callers - backup verification among them - still rely on.
    /// </summary>
    [Fact]
    public void CanOpenWithPassword_MissingFile_IsFalse()
    {
        Assert.False(StorageOptionsService.CanOpenWithPassword(_databasePath, Password));
    }

    /// <summary>
    /// Pins the wiring, not just the helper: the previous code reached past the
    /// interface for a synchronous static, and built its own connection inline in
    /// the unlock command. Either one puts the derivation back on the UI thread.
    /// </summary>
    [Fact]
    public void MainWindowViewModel_NeverVerifiesAPasswordOnItsOwnThread()
    {
        var syncVerify = typeof(StorageOptionsService)
            .GetMethod(nameof(StorageOptionsService.CanOpenWithPassword), BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(syncVerify);
        Assert.False(
            IlCallScanner.CallsMethod(typeof(MainWindowViewModel), syncVerify),
            "MainWindowViewModel calls the synchronous CanOpenWithPassword");

        // Every overload: the no-argument one is what `await connection.OpenAsync()`
        // compiles to, and it is declared on DbConnection rather than SqliteConnection.
        var openOverloads = typeof(SqliteConnection)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name is "OpenAsync" or "Open")
            .ToArray();
        Assert.NotEmpty(openOverloads);
        foreach (var overload in openOverloads)
        {
            Assert.False(
                IlCallScanner.CallsMethod(typeof(MainWindowViewModel), overload),
                $"MainWindowViewModel calls {overload.Name}; opening derives the key on the calling thread");
        }
    }

    private StorageOptionsService CreateService()
    {
        File.WriteAllText(
            _configPath,
            "{ \"databasePath\": \"" + _databasePath.Replace("\\", "\\\\") + "\" }");
        return new StorageOptionsService(new FakeDataProtectionService(), _configPath);
    }

    private async Task CreateEncryptedDatabaseAsync()
    {
        var options = new TestStorageOptionsService(_databasePath);
        options.SetInMemoryPassword(Password);
        await new DatabaseInitializer(new SqliteConnectionFactory(options), new SensitivityService())
            .InitializeAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch { }
    }
}
