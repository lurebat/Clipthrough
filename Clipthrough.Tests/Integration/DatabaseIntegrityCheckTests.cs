using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// The startup integrity check exists to stop a migration rewriting the pages of
/// an already-corrupt database. It is not free: PRAGMA quick_check reads every
/// page, so on a 352MB library it costs ~800ms with the file already in the OS
/// cache - measured at 96% of everything InitializeAsync did on an established
/// database, on every launch, to protect migrations that were not going to run.
///
/// So it runs when structural work is pending and not otherwise. Both halves
/// matter, and both are tested here against a genuinely corrupt file rather than
/// a spy, because what is being asserted is whether corruption is caught.
/// </summary>
public sealed class DatabaseIntegrityCheckTests
{
    [Fact]
    public async Task InitializeAsync_CorruptDatabaseWithAMigrationPending_RefusesToMigrateIt()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedEnoughPagesToCorruptAsync(scope);
        RewindSchemaVersion(scope);
        Checkpoint(scope);

        Corrupt(scope.DatabasePath);

        // The corrupted page belongs to this test's own table, which no migration
        // step reads - so only the full-file check can be what noticed.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.DatabaseInitializer.InitializeAsync());
        Assert.Contains("Database integrity check failed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("restore a backup", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_EstablishedDatabase_DoesNotReadEveryPageToCheckIt()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedEnoughPagesToCorruptAsync(scope);
        Checkpoint(scope);

        // Corrupt a page nothing on the established path reads, so the only thing
        // that could notice is the full-file integrity check.
        Corrupt(scope.DatabasePath);

        await scope.DatabaseInitializer.InitializeAsync();
    }

    [Fact]
    public async Task InitializeAsync_CorruptDatabaseNeedingOnlyAnFtsRebuild_RefusesToRebuildIt()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedEnoughPagesToCorruptAsync(scope);

        // Schema version stays current, so the only pending work is the FTS
        // rebuild - which still drops tables and rewrites pages.
        MakeFtsSchemaLegacy(scope);
        Checkpoint(scope);

        Corrupt(scope.DatabasePath);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.DatabaseInitializer.InitializeAsync());
        Assert.Contains("Database integrity check failed", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Puts back the single-column unicode61 search index an old build left behind.</summary>
    private static void MakeFtsSchemaLegacy(TemporaryDatabaseScope scope)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DROP TRIGGER IF EXISTS clips_ai;" +
            "DROP TRIGGER IF EXISTS clips_ad;" +
            "DROP TRIGGER IF EXISTS clips_au;" +
            "DROP TABLE IF EXISTS clips_fts;" +
            "CREATE VIRTUAL TABLE clips_fts USING fts5(content, tokenize='unicode61');";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Leaves the schema modern and healthy but marked a version behind, so the
    /// migration path runs without any of the legacy repairs failing first and
    /// masking what is being tested.
    /// </summary>
    private static void RewindSchemaVersion(TemporaryDatabaseScope scope)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_metadata SET value = '1' WHERE key = 'schema_version';";
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    /// <summary>
    /// Guards the test above from passing because the corruption was too subtle to
    /// detect rather than because the check was skipped.
    /// </summary>
    [Fact]
    public async Task TheCorruptionUsedByTheseTests_IsCorruptionQuickCheckReports()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedEnoughPagesToCorruptAsync(scope);
        Checkpoint(scope);
        Corrupt(scope.DatabasePath);

        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(5);";

        var problems = 0;
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!string.Equals(reader.GetString(0), "ok", StringComparison.Ordinal))
                {
                    problems++;
                }
            }
        }
        catch (SqliteException)
        {
            // Also a report of corruption, just an abrupt one.
            problems++;
        }

        Assert.True(problems > 0, "quick_check called the deliberately corrupted file healthy");
    }

    /// <summary>
    /// Enough rows that the file spans many pages, so overwriting bytes in the
    /// middle of it lands inside a b-tree page rather than the header.
    /// </summary>
    private static async Task SeedEnoughPagesToCorruptAsync(TemporaryDatabaseScope scope)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        // A table of this test's own, so the seeding cannot drift with the real
        // clips schema and nothing in the app reads the pages being corrupted.
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS integrity_test_filler (id INTEGER PRIMARY KEY, payload TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }

        await using var transaction = connection.BeginTransaction();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO integrity_test_filler (payload) VALUES ($payload);";
        var payload = insert.Parameters.Add("$payload", SqliteType.Text);

        var filler = new string('x', 400);
        for (var i = 0; i < 3_000; i++)
        {
            payload.Value = filler;
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>Folds the WAL into the main file so the corruption below is what gets read.</summary>
    private static void Checkpoint(TemporaryDatabaseScope scope)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static void Corrupt(string path)
    {
        SqliteConnection.ClearAllPools();
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        file.Seek(file.Length / 2, SeekOrigin.Begin);
        var garbage = new byte[256];
        Array.Fill(garbage, (byte)0xDE);
        file.Write(garbage);
    }
}
