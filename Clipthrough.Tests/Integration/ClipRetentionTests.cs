using System;
using System.Globalization;
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

        for (var i = 0; i < 6; i++)
        {
            await CaptureAsync(scope, new string((char)('a' + i), clipBytes));
        }

        var result = await scope.ClipStoreService.ApplyMaintenanceAsync();

        Assert.Equal(expectedPurgedCount, result.PurgedClipCount);
        Assert.Equal(Scalar(scope, "SELECT COUNT(*) FROM clips;"), result.TotalClipCount);
        Assert.Equal(Scalar(scope, "SELECT COALESCE(SUM(byte_size), 0) FROM clips;"), result.TotalStoredBytes);
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

    private static async Task<ClipEntry> CaptureAsync(TemporaryDatabaseScope scope, string text)
    {
        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
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
