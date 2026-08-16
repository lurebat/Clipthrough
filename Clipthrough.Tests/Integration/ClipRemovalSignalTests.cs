using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Clips leave the database by three routes - the user deleting one, the
/// retention sweep expiring old ones, and either capacity cap evicting oldest
/// first - and only the first is visible to the user. Anything holding clip
/// state outside the database, the in-memory semantic cache above all, has no
/// way to learn about the other two.
///
/// These tests pin the ids the store reports, not just that it reports
/// something: a signal that names the wrong clips is worse than none, because
/// a subscriber acts on it.
/// </summary>
public class ClipRemovalSignalTests
{
    [Fact]
    public async Task DeletingAClip_ReportsThatClipId()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(NoMaintenance());
        var store = NewStore(scope);

        var removed = new List<IReadOnlyList<long>>();
        using var subscription = store.ClipsRemoved.Subscribe(removed.Add);

        var clip = await CaptureAsync(store, "delete me");
        await store.DeleteAsync(clip!.Id);

        Assert.Equal([clip.Id], Assert.Single(removed));
    }

    /// <summary>
    /// Deleting a row that is not there must stay silent. A subscriber that
    /// evicts on every signal would otherwise drop cache entries for clips that
    /// still exist.
    /// </summary>
    [Fact]
    public async Task DeletingAMissingClip_ReportsNothing()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(NoMaintenance());
        var store = NewStore(scope);

        var removed = new List<IReadOnlyList<long>>();
        using var subscription = store.ClipsRemoved.Subscribe(removed.Add);

        await store.DeleteAsync(4242);

        Assert.Empty(removed);
    }

    /// <summary>
    /// The retention sweep deletes by predicate, so the ids it removed are known
    /// only to SQLite. This is the route the user never sees and the one a cache
    /// most needs told about.
    /// </summary>
    [Fact]
    public async Task RetentionSweep_ReportsExactlyTheExpiredClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(NoMaintenance());
        var store = NewStore(scope);

        var stale = await CaptureAsync(store, "old one");
        var fresh = await CaptureAsync(store, "new one");
        Age(scope, stale!.Id, DateTimeOffset.UtcNow.AddDays(-90));

        var removed = new List<IReadOnlyList<long>>();
        using var subscription = store.ClipsRemoved.Subscribe(removed.Add);

        scope.SettingsService.SetCurrent(NoMaintenance() with { EnableNormalClipLifetime = true, NormalClipLifetimeDays = 30 });

        var result = await store.ApplyMaintenanceAsync();

        Assert.Equal(1, result.PurgedClipCount);
        Assert.Equal([stale.Id], Assert.Single(removed));
        Assert.NotNull(await store.GetByIdAsync(fresh!.Id));
    }

    /// <summary>
    /// The entry-count cap deletes through a LIMIT subquery rather than by id,
    /// so this is the other place the removed set is SQLite's to report.
    /// </summary>
    [Fact]
    public async Task EntryCountCap_ReportsExactlyTheEvictedClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(NoMaintenance());
        var store = NewStore(scope);

        var first = await CaptureAsync(store, "oldest");
        var second = await CaptureAsync(store, "middle");
        var third = await CaptureAsync(store, "newest");
        Age(scope, first!.Id, DateTimeOffset.UtcNow.AddDays(-3));
        Age(scope, second!.Id, DateTimeOffset.UtcNow.AddDays(-2));
        Age(scope, third!.Id, DateTimeOffset.UtcNow.AddDays(-1));

        var removed = new List<IReadOnlyList<long>>();
        using var subscription = store.ClipsRemoved.Subscribe(removed.Add);

        scope.SettingsService.SetCurrent(NoMaintenance() with { EnableMaxEntryCount = true, MaxEntryCount = 1 });

        var result = await store.ApplyMaintenanceAsync();

        Assert.Equal(2, result.PurgedClipCount);
        Assert.Equal([first.Id, second.Id], Assert.Single(removed).OrderBy(id => id).ToArray());
        Assert.NotNull(await store.GetByIdAsync(third!.Id));
    }

    /// <summary>
    /// A sweep that deletes nothing - the overwhelmingly common case, since
    /// maintenance runs after every capture - must not wake subscribers.
    /// </summary>
    [Fact]
    public async Task MaintenanceThatDeletesNothing_ReportsNothing()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(NoMaintenance() with { EnableNormalClipLifetime = true, NormalClipLifetimeDays = 30 });
        var store = NewStore(scope);

        await CaptureAsync(store, "still fresh");

        var removed = new List<IReadOnlyList<long>>();
        using var subscription = store.ClipsRemoved.Subscribe(removed.Add);

        await store.ApplyMaintenanceAsync();

        Assert.Empty(removed);
    }

    private static ClipStoreService NewStore(TemporaryDatabaseScope scope)
        => new(scope.ConnectionFactory, new SensitivityService(), scope.SettingsService, scope.NotificationService);

    private static AppSettings NoMaintenance() => new()
    {
        MaxClipSizeBytes = 1_048_576,
        EnableNormalClipLifetime = false,
        EnableSensitiveClipLifetime = false,
        EnableMaxEntryCount = false,
        EnableMaxLibrarySize = false,
    };

    private static Task<ClipEntry?> CaptureAsync(ClipStoreService store, string text)
        => store.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
            SkipPostInsertMaintenance = true,
        });

    private static void Age(TemporaryDatabaseScope scope, long clipId, DateTimeOffset when)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET captured_at = $t, last_copied_at = $t WHERE id = $id;";
        command.Parameters.AddWithValue("$t", when.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", clipId);
        command.ExecuteNonQuery();
    }
}
