using System;
using System.IO;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Localization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Unlocking used to report every <see cref="SqliteException"/> as "Incorrect
/// password", so a database that had been moved, deleted or corrupted told the
/// user to retype a password that was already right.
///
/// These tests provoke the real failures through the real provider rather than
/// constructing exceptions by hand. The error codes are a property of
/// SQLitePCLRaw.bundle_e_sqlcipher, and a hand-made exception would only prove
/// that the classifier agrees with whatever number the test author typed.
/// </summary>
public sealed class SqliteErrorsTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteErrorsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Clipthrough.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "encrypted.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string ConnectionString(string path, string? password, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode };
        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        return builder.ToString();
    }

    private async Task CreateEncryptedDatabaseAsync(string password)
    {
        await using var connection = new SqliteConnection(ConnectionString(_databasePath, password, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE t(x); INSERT INTO t VALUES (1);";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteException> CaptureOpenFailureAsync(string connectionString)
    {
        return await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            await command.ExecuteScalarAsync();
        });
    }

    [Fact]
    public async Task WrongPassword_IsReportedAsAPasswordFailure()
    {
        await CreateEncryptedDatabaseAsync("correct-horse");

        var ex = await CaptureOpenFailureAsync(ConnectionString(_databasePath, "wrong-password", SqliteOpenMode.ReadOnly));

        Assert.True(SqliteErrors.IsPasswordFailure(ex));
        Assert.Equal(AppText.UnlockIncorrectPassword, SqliteErrors.DescribeUnlockFailure(ex));
    }

    [Fact]
    public async Task NoPasswordForAnEncryptedDatabase_IsReportedAsAPasswordFailure()
    {
        await CreateEncryptedDatabaseAsync("correct-horse");

        var ex = await CaptureOpenFailureAsync(ConnectionString(_databasePath, null, SqliteOpenMode.ReadOnly));

        Assert.True(SqliteErrors.IsPasswordFailure(ex));
    }

    /// <summary>
    /// The regression. A database that has been moved or deleted fails with
    /// SQLITE_CANTOPEN, which says nothing about the password - but the old
    /// catch-all blamed the password anyway.
    /// </summary>
    [Fact]
    public async Task MissingDatabase_IsNotReportedAsAPasswordFailure()
    {
        var missing = Path.Combine(_directory, "gone.db");
        Assert.False(File.Exists(missing));

        var ex = await CaptureOpenFailureAsync(ConnectionString(missing, "correct-horse", SqliteOpenMode.ReadOnly));

        Assert.False(SqliteErrors.IsPasswordFailure(ex));
        Assert.Equal(AppText.UnlockDatabaseUnreadable, SqliteErrors.DescribeUnlockFailure(ex));
    }

    /// <summary>
    /// SQLCipher cannot tell a wrong key from genuine garbage - both decrypt to
    /// nonsense and both report SQLITE_NOTADB. "Incorrect password" is the
    /// honest best guess here, and this test pins that the two really are
    /// indistinguishable rather than letting a future change assume otherwise.
    /// </summary>
    [Fact]
    public async Task FileThatIsNotADatabase_IsIndistinguishableFromAWrongPassword()
    {
        var junk = Path.Combine(_directory, "junk.db");
        await File.WriteAllTextAsync(junk, "this is definitely not a sqlite database");

        var ex = await CaptureOpenFailureAsync(ConnectionString(junk, "any-password", SqliteOpenMode.ReadOnly));

        Assert.True(SqliteErrors.IsPasswordFailure(ex));
    }

    [Fact]
    public void TheTwoMessagesDiffer()
    {
        Assert.NotEqual(AppText.UnlockIncorrectPassword, AppText.UnlockDatabaseUnreadable);
    }
}
