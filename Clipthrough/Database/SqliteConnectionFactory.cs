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

    public SqliteConnection CreateConnection()
    {
        var options = _storageOptionsService.Current;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Cache = SqliteCacheMode.Shared,
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

