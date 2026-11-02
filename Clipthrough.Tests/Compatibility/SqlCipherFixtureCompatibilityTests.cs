using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clipthrough.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Compatibility;

/// <summary>
/// U-C8 compatibility fixture. The four databases in tools/sqlite-compat/fixtures
/// were written by the SQLCipher 4.5.2 build the app ships (plaintext WAL,
/// encrypted, rekeyed, and encrypted with an uncheckpointed -wal). Every
/// installed library is in one of those shapes, so whatever encrypted-SQLite
/// package the app runs on must open them through its own connection factory
/// and read back exactly what the manifest recorded. Replacing the package
/// (docs/plans/2026-10-26-001-encrypted-sqlite-evaluation.md) has to keep this
/// green; a missed cipher recipe shows up here as error 26.
/// </summary>
public sealed class SqlCipherFixtureCompatibilityTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SqliteCompat");
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ct-sqlcompat-" + Guid.NewGuid().ToString("N"));

    public SqlCipherFixtureCompatibilityTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch { /* test cleanup */ }
    }

    private sealed record Fixture(string Name, string File, string? WalFile, string Password, long Rows, long Tags, string Checksum, Dictionary<string, long> Fts);

    private sealed record Manifest(List<Fixture> Fixtures);

    private static Manifest Load()
        => JsonSerializer.Deserialize<Manifest>(System.IO.File.ReadAllText(Path.Combine(FixtureDir, "manifest.json")))!;

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var f in Load().Fixtures) data.Add(f.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void AFixtureWrittenByTheShippedBuild_OpensThroughTheAppsFactory_WithItsDataIntact(string name)
    {
        var fixture = Load().Fixtures.Single(f => f.Name == name);
        var path = Path.Combine(_work, fixture.File);
        System.IO.File.Copy(Path.Combine(FixtureDir, fixture.File), path);
        if (fixture.WalFile is { } wal)
        {
            System.IO.File.Copy(Path.Combine(FixtureDir, wal), path + "-wal");
        }

        var storage = new TestStorageOptionsService(path);
        if (fixture.Password.Length > 0)
        {
            storage.SetInMemoryPassword(fixture.Password);
        }

        using var connection = new SqliteConnectionFactory(storage).CreateConnection();
        connection.Open();

        Assert.Equal(fixture.Rows, Scalar(connection, "SELECT count(*) FROM clips;"));
        Assert.Equal(fixture.Tags, Scalar(connection, "SELECT count(*) FROM clip_tags;"));
        Assert.Equal(fixture.Checksum, Checksum(connection));
        foreach (var (term, expected) in fixture.Fts)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM clips_fts WHERE clips_fts MATCH $q;";
            command.Parameters.AddWithValue("$q", "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            Assert.Equal(expected, Convert.ToInt64(command.ExecuteScalar()));
        }

        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", integrity.ExecuteScalar());
        }

        using (var ftsIntegrity = connection.CreateCommand())
        {
            ftsIntegrity.CommandText = "INSERT INTO clips_fts(clips_fts, rank) VALUES('integrity-check', 1);";
            ftsIntegrity.ExecuteNonQuery();
        }
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>The same digest tools/sqlite-compat/Common/Db.cs (Snap) recorded in the manifest.</summary>
    private static string Checksum(SqliteConnection connection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, content, is_sensitive FROM clips ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                hash.AppendData(Encoding.UTF8.GetBytes($"{reader.GetInt64(0)}\u001f{(reader.IsDBNull(1) ? "<null>" : reader.GetString(1))}\u001f{reader.GetInt64(2)}\n"));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, clip_id, tag FROM clip_tags ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                hash.AppendData(Encoding.UTF8.GetBytes($"T{reader.GetInt64(0)}\u001f{reader.GetInt64(1)}\u001f{reader.GetString(2)}\n"));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..32];
    }
}