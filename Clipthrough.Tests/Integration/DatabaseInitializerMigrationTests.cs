using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Opening a database that predates the current column set must still work.
///
/// The schema DDL and the column migrations are separate steps, and the DDL
/// ran first. That is fine for tables (CREATE TABLE IF NOT EXISTS is a no-op
/// on an existing clips table) but not for indexes: the sort-order indexes are
/// declared over pinned_at, last_copied_at, paste_count and byte_size, none of
/// which exist until the Ensure*Columns helpers add them. On an older database
/// the whole DDL batch aborted with "no such column", so initialisation threw
/// and the app could not open the user's history at all.
///
/// It also failed part-way through: the batch drops the clips_au trigger before
/// recreating it, so a database that survived the throw was left with no
/// FTS update trigger and a search index that silently stopped tracking edits.
/// </summary>
public sealed class DatabaseInitializerMigrationTests
{
    /// <summary>
    /// The shape clips had before aggregation, payload, tracking, pinning, OCR
    /// and lineage columns were introduced.
    /// </summary>
    private const string LegacyClipsTable = """
        CREATE TABLE clips (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            content      TEXT,
            content_type TEXT NOT NULL,
            source_app   TEXT,
            hash         TEXT NOT NULL,
            is_favorite  INTEGER NOT NULL DEFAULT 0,
            is_sensitive INTEGER NOT NULL DEFAULT 0,
            captured_at  TEXT NOT NULL
        );
        """;

    [Fact]
    public async Task InitializeAsync_DatabaseFromBeforeTheColumnMigrations_Opens()
    {
        using var scope = new TemporaryDatabaseScope();
        await CreateLegacyDatabase(scope, "clip from an old version");

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal("clip from an old version", ScalarString(scope, "SELECT content FROM clips LIMIT 1;"));
    }

    /// <summary>
    /// Every sort-order index must exist afterwards. They are only useful as a
    /// complete set - with partial coverage SQLite picks a pinned-prefixed index
    /// for an uncovered sort and fetches rows in an uncorrelated order, which
    /// measured slower than having no index at all.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_DatabaseFromBeforeTheColumnMigrations_CreatesEverySortIndex()
    {
        using var scope = new TemporaryDatabaseScope();
        await CreateLegacyDatabase(scope, "seed");

        await scope.DatabaseInitializer.InitializeAsync();

        foreach (var index in new[]
                 {
                     "idx_clips_default_order",
                     "idx_clips_oldest_order",
                     "idx_clips_paste_order",
                     "idx_clips_size_order",
                     "idx_clips_alpha_order_ci",
                     "idx_clips_retention",
                 })
        {
            Assert.Equal(
                index,
                ScalarString(scope, $"SELECT name FROM sqlite_master WHERE type = 'index' AND name = '{index}';"));
        }
    }

    /// <summary>
    /// Retention filters on a bare last_copied_at, not on the
    /// COALESCE(last_copied_at, captured_at) the sort clauses use, because only
    /// the bare form is index-usable. That equivalence rests entirely on this
    /// migration backfilling the column - a database old enough to predate it has
    /// nulls, "NULL < cutoff" is NULL, and an expired secret would then sit in the
    /// history forever with nothing to show for it.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_DatabaseFromBeforeLastCopiedAt_StillExpiresItsClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await CreateLegacyDatabase(scope, "clip captured in 2020");

        await scope.DatabaseInitializer.InitializeAsync();

        scope.SettingsService.SetCurrent(new Clipthrough.Models.AppSettings
        {
            EnableNormalClipLifetime = true,
            NormalClipLifetimeDays = 1,
            EnableSensitiveClipLifetime = false,
            EnableMaxEntryCount = false,
            EnableMaxLibrarySize = false,
        });

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Equal(0, result.TotalClipCount);
    }

    /// <summary>
    /// The FTS update trigger is dropped and recreated by the DDL batch. If the
    /// batch aborts between the two, search silently stops seeing edits.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_DatabaseFromBeforeTheColumnMigrations_KeepsTheSearchIndexTrigger()
    {
        using var scope = new TemporaryDatabaseScope();
        await CreateLegacyDatabase(scope, "seed");

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(
            "clips_au",
            ScalarString(scope, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name = 'clips_au';"));
    }

    /// <summary>
    /// The migration helpers are skipped when the stored schema version already
    /// matches the current one, so adding work to them is only effective if the
    /// constant is bumped too. A database stamped at the previous version must
    /// still receive the byte_size column - if the bump were forgotten, the
    /// migrations would be skipped and the column would never arrive.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_DatabaseStampedAtThePreviousVersion_StillGetsTheNewColumn()
    {
        using var scope = new TemporaryDatabaseScope();
        await CreateLegacyDatabase(scope, "seed");

        // Without this the test goes vacuous the day CreateLegacyDatabase starts
        // seeding byte_size: the assertion below would then hold whether or not
        // the migrations ran at all.
        Assert.DoesNotContain("byte_size", ClipColumns(scope));

        await StampSchemaVersion(scope, PreviousSchemaVersion);

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Contains("byte_size", ClipColumns(scope));
    }

    /// <summary>
    /// Index creation deliberately sits outside the schema-version gate, so an
    /// index lost for any reason comes back on the next launch rather than only
    /// when the version happens to change.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_UpToDateDatabaseMissingASortIndex_RestoresIt()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        Execute(scope, "DROP INDEX idx_clips_alpha_order_ci;");
        Assert.Null(ScalarString(scope, "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'idx_clips_alpha_order_ci';"));

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(
            "idx_clips_alpha_order_ci",
            ScalarString(scope, "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'idx_clips_alpha_order_ci';"));
    }

    /// <summary>
    /// One version behind whatever the app currently declares.
    ///
    /// This was a hardcoded 4, written when the current version was 5. The
    /// constant later went to 6 and nobody updated the copy, so the test kept
    /// passing while quietly no longer testing the thing it names: stamping 4
    /// against a current of 6 clears the gate with a version to spare, which is
    /// exactly the margin a forgotten bump eats. Read the real constant instead
    /// so this cannot drift again.
    /// </summary>
    private static int PreviousSchemaVersion => CurrentSchemaVersion - 1;

    private static int CurrentSchemaVersion =>
        (int)(typeof(Clipthrough.Database.DatabaseInitializer)
            .GetField("CurrentSchemaVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetRawConstantValue()
            ?? throw new System.InvalidOperationException(
                "DatabaseInitializer.CurrentSchemaVersion is gone or is no longer a const - this test can no longer track it."));

    private static async Task StampSchemaVersion(TemporaryDatabaseScope scope, int version)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS app_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL); " +
                              "INSERT INTO app_metadata (key, value) VALUES ('schema_version', $v) " +
                              "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$v", version.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static string[] ClipColumns(TemporaryDatabaseScope scope)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(clips);";
        using var reader = command.ExecuteReader();
        var columns = new System.Collections.Generic.List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns.ToArray();
    }

    private static void Execute(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task CreateLegacyDatabase(TemporaryDatabaseScope scope, string content)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = LegacyClipsTable +
                              "\nINSERT INTO clips (content, content_type, hash, captured_at) " +
                              "VALUES ($content, 'text', 'legacy-hash', '2020-01-01T00:00:00Z');";
        command.Parameters.AddWithValue("$content", content);
        await command.ExecuteNonQueryAsync();
    }

    private static string? ScalarString(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }
}
