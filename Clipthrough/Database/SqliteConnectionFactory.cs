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
    /// A read-only connection for maintenance probes that must not alter the
    /// database. Read-only rather than read-write is load-bearing: it cannot
    /// create an empty file when the path is wrong, so a missing database fails
    /// loudly instead of quietly reporting a healthy empty one.
    /// </summary>
    public SqliteConnection CreateReadOnlyConnection() => Create(SqliteOpenMode.ReadOnly);

    private SqliteConnection Create(SqliteOpenMode mode)
    {
        var options = _storageOptionsService.Current;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = mode,
            ForeignKeys = true,
            // Private cache (the default) is required: shared cache surfaces
            // in-process write contention as SQLITE_LOCKED, which busy_timeout
            // does NOT retry. Private cache surfaces it as SQLITE_BUSY, which
            // the 5-second busy_timeout below DOES retry.
        };

        if (!string.IsNullOrWhiteSpace(options.DatabasePassword))
        {
            builder.Password = options.DatabasePassword;
        }

        var connection = new SqliteConnection(builder.ToString());
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == System.Data.ConnectionState.Open)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA busy_timeout = 5000;";
                cmd.ExecuteNonQuery();
            }
        };
        return connection;
    }
}

