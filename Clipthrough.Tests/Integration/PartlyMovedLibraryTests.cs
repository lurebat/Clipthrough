using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// The rules a library has to obey while its payloads are only partly moved out
/// of the clips row (upgrade-start research, U3). With the move in the
/// background (U4), the app reads and writes such a library for minutes.
/// </summary>
/// <remarks>
/// A partly moved library is made the same way a crash makes one: the move runs
/// in batches of one row and is interrupted through
/// <see cref="DatabaseInitializer.AfterPayloadRowMoved"/>. The seams are static;
/// the assembly runs its tests one at a time, and they are restored in <c>finally</c>.
/// </remarks>
public sealed class PartlyMovedLibraryTests
{
    private static readonly byte[] IconA = Enumerable.Range(0, 512).Select(static i => (byte)(i * 3)).ToArray();
    private static readonly byte[] IconB = Enumerable.Range(0, 640).Select(static i => (byte)(i * 5 + 1)).ToArray();

    private static long Scalar(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static byte[] Payload(int i) => [(byte)i, 7, 7, 7];

    /// <summary>Rows in the pre-U-E1 layout: payload and icon inline.</summary>
    private static long[] SeedLegacyImages(TemporaryDatabaseScope scope, int count)
    {
        var ids = new long[count];
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        for (var i = 0; i < count; i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clips (content, content_bytes, content_type, content_format, source_app, source_app_icon,
                    hash, captured_at, first_copied_at, last_copied_at, byte_size, sensitivity_scanned_at)
                VALUES ($c, $b, 'image', 'bitmap', 'Paint', $icon, $h, $t, $t, $t, 4, $t);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$c", $"Image {i}");
            command.Parameters.AddWithValue("$b", Payload(i));
            command.Parameters.AddWithValue("$icon", IconA);
            command.Parameters.AddWithValue("$h", $"legacy-{i}");
            command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddMinutes(i).ToString("O"));
            ids[i] = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using var rewind = connection.CreateCommand();
        rewind.CommandText = "UPDATE app_metadata SET value = CAST(CAST(value AS INTEGER) - 1 AS TEXT) WHERE key = 'schema_version'; DELETE FROM app_metadata WHERE key = 'payload_move_last_id';";
        rewind.ExecuteNonQuery();
        return ids;
    }

    /// <summary>Moves the first <paramref name="moved"/> rows, then crashes the move.</summary>
    private static async Task MovePartlyAsync(TemporaryDatabaseScope scope, long[] ids, int moved)
    {
        DatabaseInitializer.PayloadMoveBatchRows = 1;
        DatabaseInitializer.AfterPayloadRowMoved = id =>
        {
            if (id == ids[moved])
            {
                throw new InvalidOperationException("injected crash mid-move");
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

        Assert.Equal(moved, Scalar(scope, "SELECT COUNT(*) FROM clip_payloads;"));
        Assert.Equal(ids.Length - moved, Scalar(scope, "SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL;"));
    }

    /// <summary>
    /// A row the move has not reached still holds its icon inline, and its
    /// stored_bytes counts that icon, as schema 8 did. The size cap has to count it
    /// - in the total, and in the walk that decides how many clips to evict - or a
    /// library mid-move is measured at a fraction of what it occupies.
    /// </summary>
    [Fact]
    public async Task TheSizeCap_CountsTheInlineIconsOfRowsNotYetMoved()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedLegacyImages(scope, 4);
        await MovePartlyAsync(scope, ids, moved: 1);

        // 3 x 400 KB of inline icons put the library past 1 MB; the payloads are 4 bytes each.
        using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE clips SET source_app_icon = zeroblob(400000) WHERE source_app_icon IS NOT NULL;
                UPDATE clips SET stored_bytes = byte_size + LENGTH(source_app_icon) WHERE source_app_icon IS NOT NULL;
                """;
            command.ExecuteNonQuery();
        }

        Assert.Equal(3 * 400_004L, Scalar(scope, "SELECT SUM(stored_bytes) FROM clips WHERE source_app_icon IS NOT NULL;"));
        scope.SettingsService.SetCurrent(new AppSettings
        {
            EnableNormalClipLifetime = false,
            EnableSensitiveClipLifetime = false,
            EnableMaxEntryCount = false,
            EnableMaxLibrarySize = true,
            MaxLibrarySizeMegabytes = 1,
        });

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        // The two oldest go: the moved one frees only its 4 bytes, the next one its
        // inline icon too. Measured by byte_size the library is 16 bytes plus one
        // stored icon and nothing is evicted; walked by byte_size it never fits and
        // everything is.
        Assert.Equal(2, result.PurgedClipCount);
        Assert.Null(await scope.ClipStoreService.GetByIdAsync(ids[0]));
        Assert.Null(await scope.ClipStoreService.GetByIdAsync(ids[1]));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(ids[2]));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(ids[3]));
        Assert.Equal(
            Scalar(scope, "SELECT COALESCE((SELECT SUM(stored_bytes) FROM clips), 0) + COALESCE((SELECT SUM(LENGTH(png)) FROM app_icons), 0);"),
            result.TotalStoredBytes);
        Assert.Equal(2 * 400_004L, result.TotalStoredBytes);
    }

    [Fact]
    public async Task EveryClip_ReadsItsPayloadAndIcon_WhereverTheyCurrentlyLive()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var ids = SeedLegacyImages(scope, 4);
        await MovePartlyAsync(scope, ids, moved: 2);

        for (var i = 0; i < ids.Length; i++)
        {
            var full = await scope.ClipStoreService.GetByIdAsync(ids[i]);
            Assert.NotNull(full);
            Assert.Equal(Payload(i), full.ContentBytes);
            Assert.Equal(IconA, full.SourceAppIconBytes);
            Assert.Equal(IconA, await scope.ClipStoreService.GetSourceAppIconAsync(ids[i]));
        }

        var page = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { Limit = 50 });
        Assert.Equal(ids.Length, page.Items.Count);
        Assert.All(page.Items, item => Assert.True(item.SourceAppIconAvailable, $"Clip {item.Id} lost its icon in the list."));

        // OCR finds images by their payload; an image the move has not reached
        // yet is still an image with a payload.
        var pendingOcr = await scope.ClipStoreService.GetPendingOcrClipIdsAsync();
        Assert.Equal(ids.OrderBy(i => i), pendingOcr.OrderBy(i => i));
    }

    /// <summary>
    /// The lost edit: a clip the move has not reached is edited, so its new payload
    /// and icon land in the new layout while the stale ones are still inline. The
    /// move must not copy the stale ones over them.
    /// </summary>
    [Fact]
    public async Task AnEditBeforeTheMoveReachesAClip_SurvivesTheMove()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });
        var ids = SeedLegacyImages(scope, 3);
        await MovePartlyAsync(scope, ids, moved: 1);

        var edited = ids[2];
        var newPayload = Encoding.UTF8.GetBytes("<html><body>edited after the upgrade began</body></html>");
        await scope.ClipStoreService.UpdateDeferredContentAsync(edited, new ClipCaptureRequest
        {
            ContentText = "edited after the upgrade began",
            ContentBytes = newPayload,
            ContentType = ContentType.RichText,
            ContentFormat = ClipContentFormat.Html,
        });
        await scope.ClipStoreService.UpdateSourceAppIconAsync(edited, IconB);

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(0, Scalar(scope, "SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL OR source_app_icon IS NOT NULL;"));
        var full = await scope.ClipStoreService.GetByIdAsync(edited);
        Assert.Equal(newPayload, full!.ContentBytes);
        Assert.Equal(IconB, full.SourceAppIconBytes);

        // The untouched clip still moved as usual.
        var other = await scope.ClipStoreService.GetByIdAsync(ids[1]);
        Assert.Equal(Payload(1), other!.ContentBytes);
        Assert.Equal(IconA, other.SourceAppIconBytes);
    }
}
