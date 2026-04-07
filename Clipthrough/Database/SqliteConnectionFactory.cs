using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Database;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory()
    {
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipthrough");

        Directory.CreateDirectory(appDataDirectory);

        var databasePath = Path.Combine(appDataDirectory, "clipthrough.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public SqliteConnection CreateConnection() => new(_connectionString);
}

