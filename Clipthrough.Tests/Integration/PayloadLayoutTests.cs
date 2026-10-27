using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// U-E1 (F-06, F-39). Payloads and source-app icons live outside the clips row,
/// icons once per distinct PNG, and existing libraries are moved there by a
/// resumable, batched migration.
/// </summary>
/// <remarks>
/// The migration tests set static seams on <see cref="DatabaseInitializer"/>. They
/// only act while rows are being moved, which no other test's fresh database ever
/// has, and they are restored in <c>finally</c>.
/// </remarks>
public sealed class PayloadLayoutTests
{
    private static readonly byte[] IconA = Enumerable.Range(0, 2048).Select(static i => (byte)(i * 7)).ToArray();

    private static async Task<long> ScalarAsync(TemporaryDatabaseScope scope, string sql)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(TemporaryDatabaseScope scope, string sql, Action<SqliteCommand>? bind = null)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<ClipEntry?> CaptureImageAsync(TemporaryDatabaseScope scope, byte[] bytes, byte[]? icon) =>
        scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = bytes,
            ContentText = "Image 4 x 3",
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            SourceApp = "Paint",
            SourceAppIconBytes = icon,
            ImageWidth = 4,
            ImageHeight = 3,
        });

    [Fact]
    public async Task ACapture_KeepsItsPayloadAndIconOutOfTheClipsRow()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var bytes = Encoding.ASCII.GetBytes("not really a png, but bytes");

        var clip = await CaptureImageAsync(scope, bytes, IconA);

        Assert.Equal(0, await ScalarAsync(scope, "SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL OR source_app_icon IS NOT NULL;"));
        Assert.Equal(1, await ScalarAsync(scope, "SELECT COUNT(*) FROM clip_payloads;"));
        var full = await scope.ClipStoreService.GetByIdAsync(clip!.Id);
        Assert.Equal(bytes, full!.ContentBytes);
        Assert.Equal(IconA, full.SourceAppIconBytes);
        Assert.Equal(IconA, await scope.ClipStoreService.GetSourceAppIconAsync(clip.Id));
    }

    [Fact]
    public async Task IdenticalIcons_AreStoredOnce()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var first = await CaptureImageAsync(scope, [1, 2, 3], IconA);
        var second = await CaptureImageAsync(scope, [4, 5, 6], IconA);

        Assert.Equal(1, await ScalarAsync(scope, "SELECT COUNT(*) FROM app_icons;"));
        Assert.Equal(IconA, (await scope.ClipStoreService.GetByIdAsync(first!.Id))!.SourceAppIconBytes);
        Assert.Equal(IconA, (await scope.ClipStoreService.GetByIdAsync(second!.Id))!.SourceAppIconBytes);
    }

    /// <summary>
    /// The cascade alone depends on the connection having foreign keys on. A
    /// payload left behind would keep a deleted - possibly sensitive - clip's
    /// content in the file with nothing pointing at it.
    /// </summary>
    [Fact]
    public async Task ADeletedClip_TakesItsPayloadWithIt_EvenWithForeignKeysOff()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var clip = await CaptureImageAsync(scope, [9, 9, 9], icon: null);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = scope.DatabasePath, ForeignKeys = false, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = OFF; DELETE FROM clips WHERE id = $id;";
            command.Parameters.AddWithValue("$id", clip!.Id);
            command.ExecuteNonQuery();
        }

        Assert.Equal(0, await ScalarAsync(scope, "SELECT COUNT(*) FROM clip_payloads;"));
    }

    [Fact]
    public async Task AnIconWhoseLastClipIsEvicted_NoLongerCounts()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { EnableMaxEntryCount = true, MaxEntryCount = 1 });

        var bigIcon = Enumerable.Range(0, 100_000).Select(static i => (byte)i).ToArray();
        await CaptureImageAsync(scope, [1], bigIcon);
        await CaptureImageAsync(scope, [2], icon: null);

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Equal(0, await ScalarAsync(scope, "SELECT COUNT(*) FROM app_icons;"));
        Assert.Equal(1, result.TotalStoredBytes);
    }

    /// <summary>Writes rows in the layout every library had before U-E1 and rewinds the schema version.</summary>
    private static async Task<long[]> SeedLegacyRowsAsync(TemporaryDatabaseScope scope, int count)
    {
        var ids = new long[count];
        for (var i = 0; i < count; i++)
        {
            await using var connection = scope.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clips (content, content_bytes, content_type, content_format, source_app, source_app_icon,
                    hash, captured_at, first_copied_at, last_copied_at, byte_size, sensitivity_scanned_at)
                VALUES ($c, $b, 'image', 'bitmap', 'Paint', $icon, $h, $t, $t, $t, 3, $t);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$c", $"Image {i}");
            command.Parameters.AddWithValue("$b", new byte[] { (byte)i, 1, 2 });
            command.Parameters.AddWithValue("$icon", IconA);
            command.Parameters.AddWithValue("$h", $"legacy-{i}");
            command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddMinutes(i).ToString("O"));
            ids[i] = (long)(await command.ExecuteScalarAsync())!;
        }

        await ExecuteAsync(scope, "UPDATE app_metadata SET value = '8' WHERE key = 'schema_version'; DELETE FROM app_metadata WHERE key = 'payload_move_last_id';");
        return ids;
    }

    private static async Task AssertMovedAsync(TemporaryDatabaseScope scope, long[] ids)
    {
        Assert.Equal(0, await ScalarAsync(scope, "SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL OR source_app_icon IS NOT NULL;"));
        Assert.Equal(ids.Length, await ScalarAsync(scope, "SELECT COUNT(*) FROM clip_payloads;"));
        Assert.Equal(1, await ScalarAsync(scope, "SELECT COUNT(*) FROM app_icons;"));
        for (var i = 0; i < ids.Length; i++)
        {
            var full = await scope.ClipStoreService.GetByIdAsync(ids[i]);
            Assert.Equal(new byte[] { (byte)i, 1, 2 }, full!.ContentBytes);
            Assert.Equal(IconA, full.SourceAppIconBytes);
        }
    }

    [Fact]
    public async Task AnExistingLibrary_IsMoved_AndMovingAgainChangesNothing()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = await SeedLegacyRowsAsync(scope, 5);

        await scope.DatabaseInitializer.InitializeAsync();
        await AssertMovedAsync(scope, ids);

        await ExecuteAsync(scope, "UPDATE app_metadata SET value = '8' WHERE key = 'schema_version';");
        await scope.DatabaseInitializer.InitializeAsync();
        await AssertMovedAsync(scope, ids);
    }

    /// <summary>
    /// A crash mid-batch: the batch in flight rolls back whole, every earlier batch
    /// stays committed, and the next start finishes the job without duplicating
    /// or losing a row.
    /// </summary>
    [Fact]
    public async Task AMoveInterruptedMidBatch_ResumesOnTheNextStart()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = await SeedLegacyRowsAsync(scope, 5);

        DatabaseInitializer.PayloadMoveBatchRows = 2;
        DatabaseInitializer.AfterPayloadRowMoved = id =>
        {
            if (id == ids[2])
            {
                throw new InvalidOperationException("injected crash in the second batch");
            }
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.DatabaseInitializer.InitializeAsync());
        }
        finally
        {
            DatabaseInitializer.AfterPayloadRowMoved = null;
            DatabaseInitializer.PayloadMoveBatchRows = 200;
        }

        // First batch committed; the second rolled back whole.
        Assert.Equal(2, await ScalarAsync(scope, "SELECT COUNT(*) FROM clip_payloads;"));
        Assert.Equal(3, await ScalarAsync(scope, "SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL;"));
        Assert.Equal(8, await ScalarAsync(scope, "SELECT CAST(value AS INTEGER) FROM app_metadata WHERE key = 'schema_version';"));

        await scope.DatabaseInitializer.InitializeAsync();

        await AssertMovedAsync(scope, ids);
        Assert.Equal(9, await ScalarAsync(scope, "SELECT CAST(value AS INTEGER) FROM app_metadata WHERE key = 'schema_version';"));
    }
}
