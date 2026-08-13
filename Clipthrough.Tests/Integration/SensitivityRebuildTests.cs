using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// A sensitivity rebuild has to scan the library without holding it in memory,
/// and has to survive a capture landing while it scans.
///
/// The rebuild used to read every clip's text into a list and scan the list
/// afterwards, so the peak cost was the whole library's text at once - as .NET
/// strings, which is roughly twice the stored size. Measured at 274MB of working
/// set for a 50k-clip library of 2KB clips, and it keeps growing with the
/// library. A rule change on a large library is the one moment a clipboard
/// manager has no business allocating hundreds of megabytes.
/// </summary>
public sealed class SensitivityRebuildTests
{
    private const int ClipCount = 1_200;
    private const int ClipChars = 64 * 1024;

    // Buffering holds all 1200 clips - ~150MB of UTF-16 - by the sampling point;
    // streaming holds one. Anything in between is noise.
    private const long AllowedGrowthBytes = 32L * 1024 * 1024;

    [Fact]
    public async Task Rebuild_DoesNotHoldTheWholeLibraryInMemory()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        Seed(scope);

        var probe = new HeapSamplingSensitivityService(scope.SensitivityService, sampleAt: ClipCount / 2);
        var store = new ClipStoreService(
            scope.ConnectionFactory,
            probe,
            scope.SettingsService,
            scope.NotificationService);

        // Taken before the call, not on the first scan: a rebuild that reads the
        // whole library up front has already allocated all of it by the time the
        // first scan runs, so a baseline sampled from inside the loop would move
        // with the defect and hide it.
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        await store.RebuildSensitivityMatchesAsync();

        Assert.True(probe.Sample > 0, $"the probe never reached clip {ClipCount / 2}; it saw {probe.ScanCount}");

        var growth = probe.Sample - baseline;
        Assert.True(
            growth < AllowedGrowthBytes,
            $"the rebuild was holding {growth / 1024 / 1024}MB of scanned text at clip {ClipCount / 2}; " +
            $"{AllowedGrowthBytes / 1024 / 1024}MB is the most it may retain");
    }

    /// <summary>
    /// A capture landing while the rebuild is scanning must not abort the rebuild.
    ///
    /// The scan reads before the rebuild writes anything, so the transaction has to
    /// be immediate. A deferred one would hold only a read snapshot until its first
    /// write, and SQLite will not upgrade a snapshot another connection has written
    /// past: the upgrade fails with "database is locked" and busy_timeout does not
    /// retry it. Measured directly - a deferred transaction that reads, lets another
    /// connection commit, then writes, fails every time.
    /// </summary>
    [Fact]
    public async Task Rebuild_SurvivesACaptureLandingMidScan()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        SeedSmall(scope, 300);

        Task? capture = null;
        var probe = new HookingSensitivityService(scope.SensitivityService, () =>
        {
            capture = Task.Run(() => Insert(scope, "intruder"));

            // Long enough for that insert to commit if nothing is holding the write
            // lock, which is exactly the situation a deferred transaction creates.
            Thread.Sleep(300);
        });

        var store = new ClipStoreService(
            scope.ConnectionFactory,
            probe,
            scope.SettingsService,
            scope.NotificationService);

        await store.RebuildSensitivityMatchesAsync();

        Assert.NotNull(capture);
        await capture!;
    }

    private static void Insert(TemporaryDatabaseScope scope, string hash)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO clips (content, content_type, hash, captured_at, first_copied_at, last_copied_at, byte_size) " +
            "VALUES ('intruder', 'text', $hash, $at, $at, $at, 8);";
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void SeedSmall(TemporaryDatabaseScope scope, int count)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO clips (content, content_type, hash, captured_at, first_copied_at, last_copied_at, byte_size) " +
            "VALUES ($content, 'text', $hash, $at, $at, $at, 40);";
        var content = command.Parameters.Add("$content", Microsoft.Data.Sqlite.SqliteType.Text);
        var hash = command.Parameters.Add("$hash", Microsoft.Data.Sqlite.SqliteType.Text);
        var at = command.Parameters.Add("$at", Microsoft.Data.Sqlite.SqliteType.Text);

        for (var i = 0; i < count; i++)
        {
            content.Value = "AKIAIOSFODNN7EXAMPLE clip " + i.ToString(CultureInfo.InvariantCulture);
            hash.Value = "small-" + i.ToString(CultureInfo.InvariantCulture);
            at.Value = DateTimeOffset.UtcNow.AddMinutes(-i).ToString("O", CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Runs a callback on the first scan, from inside the scan loop.</summary>
    private sealed class HookingSensitivityService : ISensitivityService
    {
        private readonly ISensitivityService _inner;
        private readonly Action _onFirstScan;
        private int _count;

        public HookingSensitivityService(ISensitivityService inner, Action onFirstScan)
        {
            _inner = inner;
            _onFirstScan = onFirstScan;
        }

        public IReadOnlyList<SensitivityMatch> Scan(string? content)
        {
            if (++_count == 1)
            {
                _onFirstScan();
            }

            return _inner.Scan(content);
        }

        public IReadOnlyList<SensitivityRule> GetDefaultRules() => _inner.GetDefaultRules();

        public Task<IReadOnlyList<SensitivityRule>> GetRulesAsync(CancellationToken cancellationToken = default)
            => _inner.GetRulesAsync(cancellationToken);

        public Task SaveRulesAsync(IReadOnlyList<SensitivityRule> rules, CancellationToken cancellationToken = default)
            => _inner.SaveRulesAsync(rules, cancellationToken);

        public Task ReloadAsync(CancellationToken cancellationToken = default) => _inner.ReloadAsync(cancellationToken);
    }

    private static void Seed(TemporaryDatabaseScope scope)
    {
        var filler = new string('x', ClipChars);
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
        command.Parameters.AddWithValue("$size", ClipChars);

        for (var i = 0; i < ClipCount; i++)
        {
            content.Value = filler;
            hash.Value = "hash-" + i.ToString(CultureInfo.InvariantCulture);
            at.Value = DateTimeOffset.UtcNow.AddMinutes(-i).ToString("O", CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Samples the live managed heap from inside the scan loop, which is the only
    /// place the difference is visible: by the time the rebuild returns, a buffered
    /// list has gone out of scope and looks exactly like a streamed one.
    /// Test parallelisation is off assembly-wide, so nothing else is allocating.
    /// </summary>
    private sealed class HeapSamplingSensitivityService : ISensitivityService
    {
        private readonly ISensitivityService _inner;
        private readonly int _sampleAt;

        public HeapSamplingSensitivityService(ISensitivityService inner, int sampleAt)
        {
            _inner = inner;
            _sampleAt = sampleAt;
        }

        public long Sample { get; private set; }

        public int ScanCount { get; private set; }

        public IReadOnlyList<SensitivityMatch> Scan(string? content)
        {
            ScanCount++;
            if (ScanCount == _sampleAt)
            {
                Sample = GC.GetTotalMemory(forceFullCollection: true);
            }

            return _inner.Scan(content);
        }

        public IReadOnlyList<SensitivityRule> GetDefaultRules() => _inner.GetDefaultRules();

        public Task<IReadOnlyList<SensitivityRule>> GetRulesAsync(CancellationToken cancellationToken = default)
            => _inner.GetRulesAsync(cancellationToken);

        public Task SaveRulesAsync(IReadOnlyList<SensitivityRule> rules, CancellationToken cancellationToken = default)
            => _inner.SaveRulesAsync(rules, cancellationToken);

        public Task ReloadAsync(CancellationToken cancellationToken = default) => _inner.ReloadAsync(cancellationToken);
    }
}
