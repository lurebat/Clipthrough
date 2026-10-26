using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// F-19 / OD-4a, backup half. A daily backup is kept for a week, so a
/// sensitive clip copied into one outlived its lifetime there: the audit probe
/// found the expired secret in the backup after the live library had expired
/// it. While the lifetime setting is on, backups carry no sensitive clips.
/// </summary>
public sealed class BackupSensitivePurgeTests
{
    private const string Secret = "NOT-A-REAL-SECRET-Zq81Wm";
    private const string Ordinary = "an ordinary clip that stays";

    private static async Task<(long SensitiveId, long OrdinaryId)> SeedAsync(TemporaryDatabaseScope scope)
    {
        await scope.DatabaseInitializer.InitializeAsync();
        var sensitive = await Capture(scope, Secret);
        var ordinary = await Capture(scope, Ordinary);
        await scope.ClipStoreService.SetSensitiveAsync(sensitive, true);
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO clip_embeddings (clip_id, model_version, dimensions, vector, created_at)
            VALUES ($id, 'test', 1, zeroblob(4), '2025-01-01');
            """;
        command.Parameters.AddWithValue("$id", sensitive);
        command.ExecuteNonQuery();
        return (sensitive, ordinary);
    }

    private static async Task<long> Capture(TemporaryDatabaseScope scope, string text)
    {
        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
            SkipPostInsertMaintenance = true,
        });
        return clip!.Id;
    }

    private static object? Scalar(string path, string sql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string BackupPath(TemporaryDatabaseScope scope)
        => Directory.GetFiles(Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "backups"), "*.db").Single();

    private static async Task BackUpAsync(TemporaryDatabaseScope scope, bool lifetimeOn)
    {
        scope.SettingsService.SetCurrent(AppSettings.Default with { EnableSensitiveClipLifetime = lifetimeOn });
        await new DatabaseBackupService(scope.StorageOptionsService, null, null, null, settingsService: scope.SettingsService)
            .EnsureDailyBackupAsync();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task WithTheLifetimeOn_TheDailyBackup_HoldsNoSensitiveClip_InRowsIndexVectorsOrFreePages()
    {
        using var scope = new TemporaryDatabaseScope();
        var (sensitiveId, ordinaryId) = await SeedAsync(scope);

        await BackUpAsync(scope, lifetimeOn: true);

        var backup = BackupPath(scope);
        Assert.Equal(0L, Scalar(backup, $"SELECT count(*) FROM clips WHERE id = {sensitiveId};"));
        Assert.Equal(0L, Scalar(backup, $"SELECT count(*) FROM clip_embeddings WHERE clip_id = {sensitiveId};"));
        Assert.Equal(0L, Scalar(backup, $"SELECT count(*) FROM clips_fts WHERE clips_fts MATCH '\"{Secret}\"';"));
        Assert.DoesNotContain(Secret, Encoding.ASCII.GetString(File.ReadAllBytes(backup)), StringComparison.Ordinal);
        Assert.False(File.Exists(backup + "-wal") && new FileInfo(backup + "-wal").Length > 0);

        // Everything else is a faithful copy, and the live library is untouched.
        Assert.Equal(1L, Scalar(backup, $"SELECT count(*) FROM clips WHERE id = {ordinaryId};"));
        Assert.Equal(1L, Scalar(backup, "SELECT count(*) FROM clips_fts WHERE clips_fts MATCH '\"ordinary clip\"';"));
        Assert.NotNull(await scope.ClipStoreService.GetByIdAsync(sensitiveId));
    }

    /// <summary>
    /// Owner decision on OD-4a: the purge serves the lifetime promise, so with
    /// the lifetime off sensitive clips are kept like any other and a restored
    /// backup brings them back.
    /// </summary>
    [Fact]
    public async Task WithTheLifetimeOff_TheDailyBackup_KeepsSensitiveClips()
    {
        using var scope = new TemporaryDatabaseScope();
        var (sensitiveId, _) = await SeedAsync(scope);

        await BackUpAsync(scope, lifetimeOn: false);

        var backup = BackupPath(scope);
        Assert.Equal(1L, Scalar(backup, $"SELECT count(*) FROM clips WHERE id = {sensitiveId};"));
        Assert.Equal(1L, Scalar(backup, $"SELECT count(*) FROM clip_embeddings WHERE clip_id = {sensitiveId};"));
        Assert.Equal(1L, Scalar(backup, $"SELECT count(*) FROM clips_fts WHERE clips_fts MATCH '\"{Secret}\"';"));
    }

    [Fact]
    public void ACopyWithoutTheSensitiveColumn_IsLeftAlone()
    {
        var path = Path.Combine(Path.GetTempPath(), "ct-purge-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
            using (var connection = new SqliteConnection(builder.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE clips (v TEXT); INSERT INTO clips VALUES ('x');";
                command.ExecuteNonQuery();
            }

            Assert.Equal(0, DatabaseBackupService.PurgeSensitiveClips(path, null));
            Assert.Equal(1L, Scalar(path, "SELECT count(*) FROM clips;"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}