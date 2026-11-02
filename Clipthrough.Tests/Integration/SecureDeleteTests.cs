using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// U-C4b (F-19, OD-4a). Deleting a row only unlinks its page: without
/// <c>secure_delete</c> the bytes stay in the file as a free page until
/// something overwrites them, so an expired or deleted secret could still be
/// read straight off the disk, from a backup copy of the file, or from a sync
/// client's history. The factory turns <c>secure_delete</c> on for every
/// connection it opens, which zeroes freed content as it is freed.
/// </summary>
/// <remarks>
/// Plaintext on purpose: an encrypted file never shows the text, deleted or not,
/// so only a plaintext library can show whether the bytes are gone. The WAL is
/// checkpointed and truncated first, because until then the page images in the
/// WAL still hold the original insert.
/// </remarks>
public sealed class SecureDeleteTests
{
    [Fact]
    public async Task ADeletedClipsText_IsNotLeftInTheFile()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        // Long enough to need its own pages, and unlike anything else in the file.
        var marker = "SECRET-" + Guid.NewGuid().ToString("N");
        var text = string.Concat(System.Linq.Enumerable.Repeat(marker + " ", 300));
        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = Encoding.UTF8.GetBytes(text),
            ContentText = text,
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = "Tests",
        });
        Checkpoint(scope);
        Assert.True(FileContains(scope.DatabasePath, marker), "Setup: a plaintext library should hold the clip's text before it is deleted.");

        await scope.ClipStoreService.DeleteAsync(clip!.Id);
        Checkpoint(scope);

        Assert.False(FileContains(scope.DatabasePath, marker), "The deleted clip's text is still in the database file.");
    }

    /// <summary>
    /// The guarantee has to come from the factory, not from the engine. SQLite3
    /// Multiple Ciphers (U-C9) is built with SQLITE_SECURE_DELETE, so a fresh
    /// connection already reads 1 and the test above passes without our pragma;
    /// a build without that option, or anything that turns it off, would leave
    /// freed pages readable with nothing failing. A pooled connection keeps its
    /// state between opens, so one switched off and opened again shows whether
    /// the factory re-applies it every time.
    /// </summary>
    [Fact]
    public async Task EveryOpen_TurnsSecureDeleteOn_WhateverTheConnectionHadBefore()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        using var connection = Clipthrough.Database.SqliteConnectionFactory.Create(scope.DatabasePath, password: null, SqliteOpenMode.ReadWrite, pooling: true);
        connection.Open();
        using (var off = connection.CreateCommand())
        {
            off.CommandText = "PRAGMA secure_delete = OFF; PRAGMA secure_delete;";
            Assert.Equal(0L, off.ExecuteScalar());
        }

        connection.Close();
        connection.Open();

        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA secure_delete;";
        Assert.Equal(1L, read.ExecuteScalar());
    }

    private static void Checkpoint(TemporaryDatabaseScope scope)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static bool FileContains(string path, string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes.AsSpan().IndexOf(needle) >= 0;
    }
}
