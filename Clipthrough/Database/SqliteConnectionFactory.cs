using System.IO;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Database;

public sealed class SqliteConnectionFactory
{
    private readonly IStorageOptionsService _storageOptionsService;

    public SqliteConnectionFactory(IStorageOptionsService storageOptionsService)
    {
        _storageOptionsService = storageOptionsService;
    }

    public SqliteConnection CreateConnection() => Create(SqliteOpenMode.ReadWriteCreate);

    /// <summary>
    /// Whether the database these connections open is there yet. Before the
    /// first-run "Create library" it is not, and <see cref="CreateConnection"/>
    /// would either fail to open it or create an empty file in its place.
    /// </summary>
    public bool DatabaseExists => File.Exists(_storageOptionsService.Current.DatabasePath);

    /// <summary>
    /// A read-only connection for maintenance probes that must not alter the
    /// database. Read-only rather than read-write is load-bearing: it cannot
    /// create an empty file when the path is wrong, so a missing database fails
    /// loudly instead of quietly reporting a healthy empty one.
    /// </summary>
    public SqliteConnection CreateReadOnlyConnection() => Create(SqliteOpenMode.ReadOnly);

    private SqliteConnection Create(SqliteOpenMode mode)
    {
        var options = _storageOptionsService.Current;
        return Create(
            options.DatabasePath,
            string.IsNullOrWhiteSpace(options.DatabasePassword) ? null : options.DatabasePassword,
            mode,
            foreignKeys: true);
    }

    /// <summary>
    /// The one place a <see cref="SqliteConnection"/> is built (F-28). Every
    /// connection gets the 5-second busy_timeout here, applied on each open
    /// because the pragma is per connection and not persistent. The copies this
    /// replaced drifted - f450d9a's integrity check left the pragma out and
    /// failed with "database is locked" - so ConnectionConstructionGuardTests
    /// fails if a connection or the pragma appears anywhere else.
    /// </summary>
    /// <param name="password">The cipher key; null or empty opens the file unencrypted.</param>
    /// <param name="pooling">
    /// False for connections whose file is renamed, moved or deleted afterwards:
    /// a pooled connection keeps it open after Dispose.
    /// </param>
    /// <param name="foreignKeys">Set when the caller depends on it; null leaves the provider default.</param>
    public static SqliteConnection Create(string path, string? password, SqliteOpenMode mode, bool pooling = true, bool? foreignKeys = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            // Private cache (the default) is required: shared cache surfaces
            // in-process write contention as SQLITE_LOCKED, which busy_timeout
            // does NOT retry. Private cache surfaces it as SQLITE_BUSY, which
            // the 5-second busy_timeout below DOES retry.
        };

        if (!pooling)
        {
            builder.Pooling = false;
        }

        if (foreignKeys is { } enforceForeignKeys)
        {
            builder.ForeignKeys = enforceForeignKeys;
        }

        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        var connection = new SqliteConnection(builder.ToString());
        connection.StateChange += ApplyBusyTimeoutOnOpen;
        return connection;
    }

    private static void ApplyBusyTimeoutOnOpen(object? sender, System.Data.StateChangeEventArgs e)
    {
        if (e.CurrentState == System.Data.ConnectionState.Open && sender is SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 5000;";
            cmd.ExecuteNonQuery();
        }
    }
}

