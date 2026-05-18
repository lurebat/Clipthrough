using System;
using System.IO;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Regression coverage for the storage migration path. Before commit
/// 1dce458 / d85b6f7 the migration helper copied <c>.db</c>, <c>.db-wal</c>,
/// and <c>.db-shm</c> as three raw <see cref="File.Copy"/> calls. When the
/// legacy file had uncheckpointed WAL frames the destination opened with a
/// stale <c>-shm</c> and integrity_check found every page malformed (the
/// exact incident that lost a user's recent clips during today's session).
///
/// These tests construct a database with pending WAL frames, run the public
/// migration helper, and verify the destination opens cleanly and contains
/// every row the source had.
/// </summary>
public sealed class LegacyDatabaseMigrationTests : IDisposable
{
    private readonly string _tempRoot;

    public LegacyDatabaseMigrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "clipthrough-migration-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* test cleanup */ }
    }

    [Fact]
    public void TryCopyLegacyDatabase_WithPendingWal_ProducesReadableDestinationWithAllRows()
    {
        var legacyPath = Path.Combine(_tempRoot, "legacy.db");
        var migratedPath = Path.Combine(_tempRoot, "migrated.db");

        // Two-connection trick to leave a populated .db-wal behind. SQLite
        // ordinarily checkpoints WAL into the main .db when the last
        // connection closes, which would erase exactly the scenario we want
        // to test. Holding a reader open keeps the WAL alive past the
        // writer's close.
        SqliteConnection? reader = null;
        try
        {
            SeedDatabaseWithUncheckpointedWalFrames(legacyPath, rowCount: 25, out reader);

            Assert.True(File.Exists(legacyPath + "-wal"), "Test setup should have produced a .db-wal file.");
            Assert.True(new FileInfo(legacyPath + "-wal").Length > 0,
                "Test setup should have produced uncheckpointed WAL frames.");

            StorageOptionsService.TryCopyLegacyDatabase(legacyPath, migratedPath, password: null);
        }
        finally
        {
            reader?.Dispose();
            SqliteConnection.ClearAllPools();
        }

        Assert.True(File.Exists(migratedPath), "Migration should have produced a .db file.");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = migratedPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();

        // integrity_check must report "ok". The bug we are guarding against
        // emitted "btreeInitPage()" / "unable to get the page" errors here.
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            using var reader2 = integrity.ExecuteReader();
            Assert.True(reader2.Read());
            Assert.Equal("ok", reader2.GetString(0));
            Assert.False(reader2.Read(), "integrity_check should only emit a single 'ok' row.");
        }

        // The pre-fix code paid two-fold: (a) it copied raw -wal/-shm files
        // that produced a corrupt destination, and (b) it never checkpointed
        // the legacy DB so frames that had not yet been merged were silently
        // lost. After the fix, the checkpoint is best-effort and at minimum
        // the destination should be openable and contain every row.
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM widgets;";
        var rows = Convert.ToInt32(count.ExecuteScalar());
        Assert.Equal(25, rows);
    }

    [Fact]
    public void TryCopyLegacyDatabase_DoesNotOverwriteExistingDestination()
    {
        var legacyPath = Path.Combine(_tempRoot, "legacy.db");
        var migratedPath = Path.Combine(_tempRoot, "migrated.db");

        SqliteConnection? reader = null;
        try
        {
            SeedDatabaseWithUncheckpointedWalFrames(legacyPath, rowCount: 5, out reader);
            File.WriteAllText(migratedPath, "existing");

            StorageOptionsService.TryCopyLegacyDatabase(legacyPath, migratedPath, password: null);
        }
        finally
        {
            reader?.Dispose();
            SqliteConnection.ClearAllPools();
        }

        Assert.Equal("existing", File.ReadAllText(migratedPath));
    }

    /// <summary>
    /// Creates a SQLite database in WAL journaling mode with the specified
    /// number of rows committed to the WAL. Returns a still-open reader
    /// connection via <paramref name="readerToKeepAlive"/>: the caller must
    /// dispose it after the migration runs. As long as the reader is alive,
    /// SQLite leaves the .db-wal file in place even when the writer closes,
    /// which is exactly the post-crash situation the migration helper has to
    /// cope with.
    /// </summary>
    private static void SeedDatabaseWithUncheckpointedWalFrames(string path, int rowCount, out SqliteConnection readerToKeepAlive)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        var writer = new SqliteConnection(builder.ToString());
        writer.Open();
        try
        {
            using (var pragma = writer.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode = WAL;";
                pragma.ExecuteNonQuery();
            }

            using (var schema = writer.CreateCommand())
            {
                schema.CommandText = "CREATE TABLE widgets (id INTEGER PRIMARY KEY, label TEXT NOT NULL);";
                schema.ExecuteNonQuery();
            }

            using (var tx = writer.BeginTransaction())
            {
                using var insert = writer.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO widgets (id, label) VALUES ($id, $label);";
                var idParam = insert.Parameters.Add("$id", SqliteType.Integer);
                var labelParam = insert.Parameters.Add("$label", SqliteType.Text);

                for (var i = 1; i <= rowCount; i++)
                {
                    idParam.Value = i;
                    labelParam.Value = $"row-{i}";
                    insert.ExecuteNonQuery();
                }
                tx.Commit();
            }

            // Open the reader before closing the writer; the reader will hold
            // a shared lock on the WAL so SQLite skips the last-close
            // checkpoint and leaves the .db-wal file behind.
            readerToKeepAlive = new SqliteConnection(builder.ToString());
            readerToKeepAlive.Open();

            using (var sanity = readerToKeepAlive.CreateCommand())
            {
                sanity.CommandText = "SELECT COUNT(*) FROM widgets;";
                Assert.Equal(rowCount, Convert.ToInt32(sanity.ExecuteScalar()));
            }
        }
        finally
        {
            writer.Dispose();
        }
    }
}
