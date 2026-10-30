using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Regression coverage for retention pruning and user-kept clips.
///
/// Every prune path — age, entry count, and library size — previously deleted
/// by timestamp alone, so a pinned or favorited clip was evicted like any
/// other. Maintenance runs after every capture, so a busy hour of copying was
/// enough to silently destroy clips the user had explicitly marked to keep.
///
/// The sensitive-clip timer is the deliberate exception: expiring a secret
/// outranks the user's intent to keep it.
/// </summary>
public sealed class ClipRetentionTests
{
    [Fact]
    public async Task Maintenance_EntryCountCap_PreservesPinnedAndFavoriteClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableMaxEntryCount = true,
            MaxEntryCount = 2,
        });

        var pinned = await CaptureAsync(scope, "pinned keeper");
        var favorite = await CaptureAsync(scope, "favorite keeper");
        var ordinaryOld = await CaptureAsync(scope, "ordinary old");
        var ordinaryNew = await CaptureAsync(scope, "ordinary new");

        await scope.ClipStoreService.SetPinnedAsync(pinned.Id, true);
        await scope.ClipStoreService.SetFavoriteAsync(favorite.Id, true);

        await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(pinned.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(favorite.Id));
        Assert.Null(await scope.ClipStoreService.GetByIdAsync(ordinaryOld.Id));
        Assert.Null(await scope.ClipStoreService.GetByIdAsync(ordinaryNew.Id));
    }

    [Fact]
    public async Task Maintenance_NormalLifetime_PreservesPinnedAndFavoriteClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableNormalClipLifetime = true,
            NormalClipLifetimeDays = 7,
        });

        var pinned = await CaptureAsync(scope, "pinned keeper");
        var favorite = await CaptureAsync(scope, "favorite keeper");
        var ordinary = await CaptureAsync(scope, "ordinary");

        await scope.ClipStoreService.SetPinnedAsync(pinned.Id, true);
        await scope.ClipStoreService.SetFavoriteAsync(favorite.Id, true);
        Backdate(scope, DateTimeOffset.UtcNow.AddDays(-30));

        await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(pinned.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(favorite.Id));
        Assert.Null(await scope.ClipStoreService.GetByIdAsync(ordinary.Id));
    }

    [Fact]
    public async Task Maintenance_LibrarySizeCap_PreservesPinnedAndFavoriteClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableMaxLibrarySize = true,
            MaxLibrarySizeMegabytes = 1,
        });

        var pinned = await CaptureAsync(scope, "pinned keeper");
        var favorite = await CaptureAsync(scope, "favorite keeper");
        var ordinary = await CaptureAsync(scope, "ordinary");

        await scope.ClipStoreService.SetPinnedAsync(pinned.Id, true);
        await scope.ClipStoreService.SetFavoriteAsync(favorite.Id, true);

        // Push every clip well past the 1 MB cap so the size sweep has to evict.
        Execute(scope, "UPDATE clips SET byte_size = 900000;");

        await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(pinned.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(favorite.Id));
        Assert.Null(await scope.ClipStoreService.GetByIdAsync(ordinary.Id));
    }

    /// <summary>
    /// The library size cap used to measure only byte_size, which counts a clip's
    /// own content and nothing else. Every row also stores the source app's icon -
    /// measured at 2.7KB, several times the size of a typical text clip. On a
    /// 4,000-clip library that made the cap under-count by 12.5x: 1.6MB counted
    /// against 20MB on disk, so "Max library size: 500 MB" bounded nothing near it.
    /// </summary>
    [Fact]
    public async Task Maintenance_LibrarySizeCap_CountsTheSourceAppIconsItStores()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableMaxLibrarySize = true,
            MaxLibrarySizeMegabytes = 1,
        });

        var oldest = await CaptureAsync(scope, "oldest");
        var newest = await CaptureAsync(scope, "newest");

        // Content alone fits the 1 MB cap. The icon is what pushes the pair past it,
        // so nothing is evicted unless it is counted - and since U-E1 both clips
        // share one stored copy, so it counts once (2 x 400 KB + 300 KB > 1 MB).
        Execute(scope, "UPDATE clips SET byte_size = 400000;");
        var icon = new byte[300_000];
        new Random(3).NextBytes(icon);
        await scope.ClipStoreService.UpdateSourceAppIconAsync(oldest.Id, icon);
        await scope.ClipStoreService.UpdateSourceAppIconAsync(newest.Id, icon);

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Null(await scope.ClipStoreService.GetByIdAsync(oldest.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(newest.Id));

        // And the size it reports is the size it evicted against: the survivor's
        // payload and the icon it still uses, once.
        Assert.Equal(400_000 + 300_000, result.TotalStoredBytes);
    }

    /// <summary>
    /// The size sweep decides *how many* clips to evict by walking rows and
    /// subtracting until the library fits, then hands that count to the delete -
    /// which drops the oldest non-kept clips. Both halves have to agree on which
    /// rows are evictable. If the walk counts pinned and favorited rows that the
    /// delete will never touch, it subtracts bytes no eviction can reclaim and
    /// stops early, leaving the library over its cap with evictable clips still
    /// in it.
    ///
    /// It only shows when a kept clip is large enough to carry the total past the
    /// cap on its own: with equal sizes the walk exhausts the non-kept rows either
    /// way and the miscount is invisible. Hence one 10MB pinned clip against two
    /// small ordinary ones - counting the pinned row satisfies the cap after a
    /// single subtraction, so only one of the two gets evicted.
    /// </summary>
    [Fact]
    public async Task Maintenance_LibrarySizeCap_CountsOnlyTheClipsItCanActuallyEvict()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableMaxLibrarySize = true,
            MaxLibrarySizeMegabytes = 1,
        });

        var pinned = await CaptureAsync(scope, "pinned whale");
        var older = await CaptureAsync(scope, "older ordinary");
        var newer = await CaptureAsync(scope, "newer ordinary");

        await scope.ClipStoreService.SetPinnedAsync(pinned.Id, true);

        Execute(scope, $"UPDATE clips SET byte_size = 400000 WHERE id IN ({older.Id}, {newer.Id});");
        Execute(scope, $"UPDATE clips SET byte_size = 10000000 WHERE id = {pinned.Id};");

        await scope.ClipStoreService.ApplyMaintenanceAsync();

        // The pinned clip alone exceeds the cap, so the library cannot get under
        // it - but every clip that *could* have been evicted still has to go.
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(pinned.Id));
        Assert.Null(await scope.ClipStoreService.GetByIdAsync(older.Id));
        Assert.Null(await scope.ClipStoreService.GetByIdAsync(newer.Id));
    }

    /// <summary>
    /// Since U-E1 the size total counts each distinct icon once, so evicting the
    /// last clip that uses an icon frees the icon as well, and the orphan sweep
    /// deletes it. The walk that decides how many clips to evict subtracted only
    /// each row's stored_bytes, so it kept going after the library already fit:
    /// here, evicting the oldest clip and its 4 KB icon is enough, but the walk
    /// saw only 100 bytes come off and took a 524 KB clip as well. (Rubber-duck
    /// F-11-1)
    /// </summary>
    [Fact]
    public async Task Maintenance_LibrarySizeCap_CountsTheIconAnEvictionFrees()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableMaxLibrarySize = true,
            MaxLibrarySizeMegabytes = 1,
        });

        var oldest = await CaptureAsync(scope, "oldest, with an icon of its own");
        var middle = await CaptureAsync(scope, "middle");
        var newest = await CaptureAsync(scope, "newest");

        // 100 + 4096 + 2 x 524,000 = 1,052,196: over the 1,048,576 cap by 3,620,
        // which the oldest clip's payload alone (100) does not cover and its
        // payload plus its icon (4,196) does.
        Execute(scope, $"UPDATE clips SET byte_size = 100 WHERE id = {oldest.Id};");
        Execute(scope, $"UPDATE clips SET byte_size = 524000 WHERE id IN ({middle.Id}, {newest.Id});");
        await scope.ClipStoreService.UpdateSourceAppIconAsync(oldest.Id, Enumerable.Repeat((byte)7, 4096).ToArray());

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Null(await scope.ClipStoreService.GetByIdAsync(oldest.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(middle.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(newest.Id));
        Assert.Equal(1, result.PurgedClipCount);
        Assert.Equal(2 * 524_000, result.TotalStoredBytes);
        // Freed by the eviction itself, so only the sweep after it can remove it.
        Assert.Equal(0, Scalar(scope, "SELECT COUNT(*) FROM app_icons;"));
    }

    /// <summary>
    /// The same over-eviction from the other side: an earlier purge in the same
    /// maintenance pass - here the entry-count cap - orphans an icon, and the
    /// orphan sweep used to run only after the size cap had read its total. The
    /// orphan's bytes counted against the cap, and two clips the library had room
    /// for were evicted to pay for an icon about to be deleted anyway.
    /// (Rubber-duck F-11-1)
    /// </summary>
    [Fact]
    public async Task Maintenance_LibrarySizeCap_DoesNotCountAnIconAnEarlierPurgeOrphaned()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableMaxEntryCount = true,
            MaxEntryCount = 3,
            EnableMaxLibrarySize = true,
            MaxLibrarySizeMegabytes = 1,
        });

        var overCount = await CaptureAsync(scope, "evicted by the entry count, with an icon of its own");
        var small = await CaptureAsync(scope, "small");
        var middle = await CaptureAsync(scope, "middle");
        var newest = await CaptureAsync(scope, "newest");

        // Once the count cap has taken the first clip, what is left is
        // 100 + 2 x 524,000 = 1,048,100, under the 1,048,576 cap - unless the
        // first clip's 4 KB icon is still counted.
        Execute(scope, $"UPDATE clips SET byte_size = 100 WHERE id IN ({overCount.Id}, {small.Id});");
        Execute(scope, $"UPDATE clips SET byte_size = 524000 WHERE id IN ({middle.Id}, {newest.Id});");
        await scope.ClipStoreService.UpdateSourceAppIconAsync(overCount.Id, Enumerable.Repeat((byte)7, 4096).ToArray());

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Null(await scope.ClipStoreService.GetByIdAsync(overCount.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(small.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(middle.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(newest.Id));
        Assert.Equal(1, result.PurgedClipCount);
        Assert.Equal(100 + (2 * 524_000), result.TotalStoredBytes);
    }

    /// <summary>
    /// The size a purge reports leaves out an icon nothing referred to before the
    /// pass began - re-copying a clip from another app leaves one - because the
    /// sweep removes it before the size cap reads its total.
    /// </summary>
    [Fact]
    public async Task Maintenance_LibrarySizeCap_ReportsTheSizeLeftAfterTheOrphanSweep()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableMaxLibrarySize = true,
            MaxLibrarySizeMegabytes = 1,
        });

        var oldest = await CaptureAsync(scope, "oldest");
        var newest = await CaptureAsync(scope, "newest");
        Execute(scope, "UPDATE clips SET byte_size = 600000;");
        Execute(scope, "INSERT INTO app_icons (hash, png) VALUES ('orphan', zeroblob(4096));");

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Null(await scope.ClipStoreService.GetByIdAsync(oldest.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(newest.Id));
        Assert.Equal(0, Scalar(scope, "SELECT COUNT(*) FROM app_icons;"));
        Assert.Equal(600_000, result.TotalStoredBytes);
    }

    /// <summary>
    /// Re-copying a clip from a different app replaces its icon hash and leaves
    /// the old icon in app_icons with nothing referring to it. The orphan sweep ran
    /// only after a pass had deleted clips, so that icon kept counting toward the
    /// size cap - here it tips a library that fits over the cap and evicts a clip -
    /// and toward the reported total, cap or no cap, until some deletion or a
    /// restart swept it. (Rubber-duck F-11-3)
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Maintenance_DoesNotCountTheIconAReCopyReplaced(bool capSize)
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableMaxEntryCount = false,
            EnableMaxLibrarySize = capSize,
            MaxLibrarySizeMegabytes = 1,
        });

        var older = await CaptureAsync(scope, "older");
        var recopied = await CaptureAsync(scope, "copied from two apps", Enumerable.Repeat((byte)1, 4096).ToArray());
        Assert.Equal(recopied.Id, (await CaptureAsync(scope, "copied from two apps", Enumerable.Repeat((byte)2, 300).ToArray())).Id);

        // 2 x 524,000 + the current 300-byte icon = 1,048,300, under the
        // 1,048,576 cap; the replaced 4 KB icon would take it over.
        Execute(scope, "UPDATE clips SET byte_size = 524000;");

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(older.Id));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(recopied.Id));
        Assert.Equal(0, result.PurgedClipCount);
        Assert.Equal(1, Scalar(scope, "SELECT COUNT(*) FROM app_icons;"));
        Assert.Equal((2 * 524_000) + 300, result.TotalStoredBytes);
    }

    /// <summary>
    /// The deliberate exception. A pinned secret still expires — pinning must not
    /// become a way to opt out of the sensitive-clip timer.
    /// </summary>
    [Fact]
    public async Task Maintenance_SensitiveLifetime_StillExpiresPinnedAndFavoriteClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableSensitiveClipLifetime = true,
            SensitiveClipLifetimeMinutes = 5,
        });

        var pinned = await CaptureAsync(scope, "pinned secret");
        var favorite = await CaptureAsync(scope, "favorite secret");

        await scope.ClipStoreService.SetPinnedAsync(pinned.Id, true);
        await scope.ClipStoreService.SetFavoriteAsync(favorite.Id, true);
        Execute(scope, "UPDATE clips SET is_sensitive = 1;");
        Backdate(scope, DateTimeOffset.UtcNow.AddHours(-1));

        await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Null(await scope.ClipStoreService.GetByIdAsync(pinned.Id));
        Assert.Null(await scope.ClipStoreService.GetByIdAsync(favorite.Id));
    }

    /// <summary>
    /// Maintenance reports the clip count and library size from numbers the
    /// capacity checks already computed rather than re-running COUNT and SUM,
    /// which are full scans it would otherwise pay for twice on every capture.
    /// That is only sound while the arithmetic tracks every purge, and the
    /// arithmetic is the easy thing to get wrong: a step whose deletions are never
    /// subtracted leaves the status bar quietly reporting a library that no longer
    /// exists.
    ///
    /// expectedPurgedCount is asserted as well as the totals, because the totals
    /// alone cannot tell "the arithmetic is right" from "no purge ran". The first
    /// draft of this test set MaxLibrarySizeMegabytes to 0 and never noticed that
    /// AppSettings.Normalize clamps it to 1MB, so the size-cap cases were passing
    /// without ever deleting anything.
    /// </summary>
    [Theory]
    // Caps enabled but never reached: the carried numbers are used unpurged.
    [InlineData(true, 100, true, 64, SmallClipBytes, 0)]
    // Both caps off: nothing is carried, so the totals come from the fallback reads.
    [InlineData(false, 100, false, 64, SmallClipBytes, 0)]
    // Entry-count cap only.
    [InlineData(true, 2, false, 64, SmallClipBytes, 4)]
    // Library-size cap only, which leaves the clip count to the fallback read.
    [InlineData(false, 100, true, 1, LargeClipBytes, 3)]
    // Both caps bite, so the carried count has to survive two separate purges.
    [InlineData(true, 5, true, 1, LargeClipBytes, 3)]
    public async Task Maintenance_ReportsTheSameTotalsTheDatabaseWould(
        bool capCount,
        int maxEntryCount,
        bool capSize,
        int maxMegabytes,
        int clipBytes,
        int expectedPurgedCount)
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 1_000_000,
            EnableNormalClipLifetime = false,
            EnableSensitiveClipLifetime = false,
            EnableMaxEntryCount = capCount,
            MaxEntryCount = maxEntryCount,
            EnableMaxLibrarySize = capSize,
            MaxLibrarySizeMegabytes = maxMegabytes,
        });

        // Each clip has an icon of its own, so a purge also leaves icons behind for
        // the orphan sweep (U-E1), and the reported total has to be read after it.
        // Without icons the row walk and the database agree whatever is read when.
        for (var i = 0; i < 6; i++)
        {
            var clip = await CaptureAsync(scope, new string((char)('a' + i), clipBytes));
            await scope.ClipStoreService.UpdateSourceAppIconAsync(clip.Id, Enumerable.Repeat((byte)(i + 1), 1000).ToArray());
        }

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Equal(expectedPurgedCount, result.PurgedClipCount);
        Assert.Equal(Scalar(scope, "SELECT COUNT(*) FROM clips;"), result.TotalClipCount);
        Assert.Equal(6 - expectedPurgedCount, Scalar(scope, "SELECT COUNT(*) FROM app_icons;"));
        Assert.Equal(
            Scalar(scope, "SELECT COALESCE((SELECT SUM(stored_bytes) FROM clips), 0) + COALESCE((SELECT SUM(LENGTH(png)) FROM app_icons), 0);"),
            result.TotalStoredBytes);
    }

    /// <summary>
    /// A single size-cap purge has to be able to evict more clips than SQLite will
    /// accept parameters for.
    ///
    /// The eviction used to name every doomed clip in a "WHERE id IN (...)" list,
    /// one SQL parameter each. Past SQLITE_MAX_VARIABLE_NUMBER - 32766 - that
    /// throws "too many SQL variables", and the purge that trips it is exactly the
    /// one a real user hits: lowering the size cap on a large library, or importing
    /// one. Maintenance runs after every capture, so the throw does not cost one
    /// purge. Retention stops working entirely and sensitive clips stop expiring,
    /// with nothing on screen to say so.
    ///
    /// Seeded through SQL rather than the capture path: 45k captures would take
    /// minutes, and what is under test is the delete, not the insert.
    /// </summary>
    [Fact]
    public async Task Maintenance_SizeCap_EvictsMoreClipsThanSqliteAllowsParameters()
    {
        // 4.5MB against a 1MB cap leaves ~10k clips, so ~35k have to go at once.
        const int clipCount = 45_000;
        const int clipBytes = 100;

        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedClips(scope, clipCount, clipBytes);

        scope.SettingsService.SetCurrent(new AppSettings
        {
            EnableNormalClipLifetime = false,
            EnableSensitiveClipLifetime = false,
            EnableMaxEntryCount = false,
            EnableMaxLibrarySize = true,
            MaxLibrarySizeMegabytes = 1,
        });

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.True(
            result.PurgedClipCount > 32_766,
            $"the purge has to cross SQLite's parameter limit to test anything; it evicted {result.PurgedClipCount}");
        Assert.True(result.TotalStoredBytes <= 1024L * 1024L);
        Assert.Equal(Scalar(scope, "SELECT COUNT(*) FROM clips;"), result.TotalClipCount);
        Assert.Equal(Scalar(scope, "SELECT COALESCE(SUM(byte_size), 0) FROM clips;"), result.TotalStoredBytes);
    }

    private static void SeedClips(TemporaryDatabaseScope scope, int count, int byteSize)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO clips (content, content_type, hash, captured_at, first_copied_at, last_copied_at, byte_size) " +
            "VALUES ($content, 'text', $hash, $at, $at, $at, $size);";
        var content = command.Parameters.Add("$content", Microsoft.Data.Sqlite.SqliteType.Text);
        var hash = command.Parameters.Add("$hash", Microsoft.Data.Sqlite.SqliteType.Text);
        var at = command.Parameters.Add("$at", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.AddWithValue("$size", byteSize);

        for (var i = 0; i < count; i++)
        {
            content.Value = "clip " + i.ToString(CultureInfo.InvariantCulture);
            hash.Value = "hash-" + i.ToString(CultureInfo.InvariantCulture);
            at.Value = DateTimeOffset.UtcNow.AddMinutes(-i).ToString("O", CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private const int SmallClipBytes = 16;

    // Six of these is 1.75MB, so a 1MB cap has to evict three of them.
    private const int LargeClipBytes = 300 * 1024;

    private static long Scalar(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static async Task<ClipEntry> CaptureAsync(TemporaryDatabaseScope scope, string text, byte[]? icon = null)
    {
        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
            SourceAppIconBytes = icon,
            SkipPostInsertMaintenance = true,
        });

        Assert.NotNull(clip);
        return clip!;
    }

    private static void Backdate(TemporaryDatabaseScope scope, DateTimeOffset timestamp)
    {
        var value = timestamp.ToString("O", CultureInfo.InvariantCulture);
        Execute(scope, $"UPDATE clips SET captured_at = '{value}', last_copied_at = '{value}';");
    }

    private static void Execute(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
