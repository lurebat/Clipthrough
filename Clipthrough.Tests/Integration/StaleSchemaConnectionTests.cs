using System;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Database;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Live check F2: the first maintenance during a deferred upgrade's background
/// move failed with "SQLite Error 1: 'no such table: clips'", and the periodic
/// maintenance stopped for the session.
/// </summary>
/// <remarks>
/// The cause is in SQLite itself (reproduced on 3.50.4 and 3.54.0, without this
/// app): when another connection has changed the schema, the first DELETE on a
/// connection holding the old one fails to prepare if the table has an FTS5
/// delete trigger and foreign keys are on. The move's DROP INDEX is such a
/// change, and a pooled connection opened before it keeps the old schema. The
/// factory now brings each connection's schema up to date when it is opened.
/// </remarks>
public sealed class StaleSchemaConnectionTests
{
    private static void Execute(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task APooledConnection_StillDeletesClips_AfterAnotherConnectionChangedTheSchema()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        Execute(scope, """
            INSERT INTO clips (content, content_type, content_format, hash, captured_at, first_copied_at, last_copied_at, byte_size, is_sensitive, sensitivity_scanned_at)
            VALUES ('expired secret', 'text', 'plain', 'h1', '2000-01-01T00:00:00.0000000+00:00', '2000-01-01T00:00:00.0000000+00:00', '2000-01-01T00:00:00.0000000+00:00', 14, 1, '2000-01-01T00:00:00.0000000+00:00');
            """);

        // Held open so the read below takes a different connection from the pool,
        // which keeps the schema as it is now.
        using var other = scope.ConnectionFactory.CreateConnection();
        other.Open();
        _ = await scope.ClipStoreService.GetByIdAsync(1);

        // Another connection changes the schema, as the background move does.
        using (var ddl = other.CreateCommand())
        {
            ddl.CommandText = "DROP INDEX idx_clips_ocr_images;";
            ddl.ExecuteNonQuery();
        }

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Equal(1, result.PurgedClipCount);
    }

    /// <summary>The live failure: the first maintenance lands while the background move runs.</summary>
    [Fact]
    public async Task TheFirstMaintenance_DuringTheBackgroundMove_Succeeds()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        Execute(scope, """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40)
            INSERT INTO clips (content, content_bytes, content_type, content_format, source_app, source_app_icon,
                hash, captured_at, first_copied_at, last_copied_at, byte_size, sensitivity_scanned_at)
            SELECT 'Image ' || i, randomblob(4096), 'image', 'bitmap', 'Paint', x'01020304', 'v8-' || i,
                strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 4096,
                strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            FROM n;
            UPDATE clips SET stored_bytes = byte_size + LENGTH(source_app_icon);
            UPDATE app_metadata SET value = '8' WHERE key = 'schema_version';
            DELETE FROM app_metadata WHERE key IN ('payload_move_last_id', 'payload_move_done');
            """);
        await scope.DatabaseInitializer.InitializeAsync(progress: null, allowDeferredPayloadMove: true);
        _ = await scope.ClipStoreService.GetByIdAsync(1);

        var moved = 0;
        using var reached = new ManualResetEventSlim();
        DatabaseInitializer.PayloadMoveBatchRows = 5;
        DatabaseInitializer.AfterPayloadRowMoved = _ =>
        {
            if (Interlocked.Increment(ref moved) == 12)
            {
                reached.Set();
            }
        };
        try
        {
            var move = Task.Run(() => scope.DatabaseInitializer.RunDeferredPayloadMoveAsync(progress: null));
            Assert.True(reached.Wait(TimeSpan.FromSeconds(30)), "The background move never got under way.");

            var maintenance = await Record.ExceptionAsync(() => scope.ClipStoreService.ApplyMaintenanceAsync());

            Assert.True(await move);
            Assert.Null(maintenance);
        }
        finally
        {
            DatabaseInitializer.AfterPayloadRowMoved = null;
            DatabaseInitializer.PayloadMoveBatchRows = 200;
        }
    }
}
