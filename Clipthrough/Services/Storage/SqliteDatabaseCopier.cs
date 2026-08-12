using System;
using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

/// <summary>
/// Copies a live (possibly encrypted) SQLite database to a new file using the
/// online backup API rather than a checkpoint followed by a raw file copy.
///
/// The checkpoint-then-copy approach it replaces looked safe and was not.
/// <c>PRAGMA wal_checkpoint(TRUNCATE)</c> does not throw when it cannot
/// finish - it returns a <c>(busy, log, checkpointed)</c> row, and any reader
/// holding an older snapshot makes it come back <c>busy = 1</c> with frames
/// still in the WAL. Callers that ran it through <c>ExecuteNonQuery</c>
/// discarded that row, so they could not tell a completed checkpoint from an
/// abandoned one. Copying just the main <c>.db</c> file afterwards does not
/// merely lose the un-flushed commits: the result is a file whose header
/// disagrees with its pages, and opening it fails outright with
/// <c>SQLITE_CORRUPT</c> - "database disk image is malformed". A backup like
/// that is worse than no backup, because nothing notices until the day someone
/// tries to restore it.
///
/// <see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/> reads
/// through SQLite itself, so WAL-resident commits are included by construction
/// and no checkpoint is needed. It works with SQLCipher provided the
/// destination is opened with the same key, which also keeps the copy
/// encrypted. Pooling is disabled on both ends so neither file is still held
/// open when this returns and the caller can immediately move or delete it.
/// </summary>
internal static class SqliteDatabaseCopier
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="destinationPath"/>,
    /// overwriting any existing destination. Throws <see cref="SqliteException"/>
    /// if the source cannot be opened (wrong or missing password, locked file)
    /// and <see cref="IOException"/> on file-system failures. A destination left
    /// behind by a failed attempt is removed, so a partial copy is never
    /// mistaken for a usable one.
    /// </summary>
    public static void CopyDatabase(string sourcePath, string? password, string destinationPath)
    {
        // The backup API overwrites the destination's contents, but it has to
        // open it first. A leftover file from an interrupted copy - truncated,
        // or still keyed to a password that has since changed - fails to open
        // and would block every future copy to this path, so clear it.
        File.Delete(destinationPath);

        try
        {
            using var source = OpenConnection(sourcePath, password);
            using var destination = OpenConnection(destinationPath, password);
            source.BackupDatabase(destination);
        }
        catch
        {
            TryDeletePartialCopy(destinationPath);
            throw;
        }
    }

    private static SqliteConnection OpenConnection(string path, string? password)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Without this the connection returns to the pool on Dispose and
            // keeps the file open, which breaks the move/delete that follows.
            Pooling = false,
        };
        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        var connection = new SqliteConnection(builder.ToString());
        connection.StateChange += ApplyBusyTimeoutOnOpen;
        try
        {
            connection.Open();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    private static void TryDeletePartialCopy(string destinationPath)
    {
        try
        {
            File.Delete(destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reporting the original failure matters more than this cleanup.
            System.Diagnostics.Trace.TraceWarning(
                $"Could not remove the partial database copy '{destinationPath}': {ex.Message}");
        }
    }

    private static void ApplyBusyTimeoutOnOpen(object? sender, StateChangeEventArgs e)
    {
        if (e.CurrentState == ConnectionState.Open && sender is SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 5000;";
            cmd.ExecuteNonQuery();
        }
    }
}
