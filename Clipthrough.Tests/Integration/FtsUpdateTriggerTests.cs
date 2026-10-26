using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// F-26. clips_au re-tokenised a clip into FTS whenever an indexed column was
/// *assigned*, changed or not. Re-copying a clip and the deferred HTML/RTF
/// update both assign the same text again, so a 1.8 MB clip cost 714-763 ms
/// under the write lock for an index that ends up identical.
/// </summary>
public sealed class FtsUpdateTriggerTests
{
    private static async Task<SqliteConnection> SeedAsync(TemporaryDatabaseScope scope)
    {
        await scope.DatabaseInitializer.InitializeAsync();
        var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await Exec(connection, """
            INSERT INTO clips (id, hash, content_type, content_format, content, source_app, source_url, byte_size,
                               captured_at, first_copied_at, last_copied_at)
            VALUES (1, 'h1', 'text', 'text', 'alpha bravo charlie', 'notepad.exe', NULL, 19,
                    '2024-01-01', '2024-01-01', '2024-01-01');
            """);
        return connection;
    }

    private static async Task Exec(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> FtsShadow(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(id || ':' || hex(block), ',') FROM (SELECT id, block FROM clips_fts_data ORDER BY id);";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> Matches(SqliteConnection connection, string term)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM clips_fts WHERE clips_fts MATCH $q;";
        command.Parameters.AddWithValue("$q", "\"" + term + "\"");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AssigningTheSameIndexedText_DoesNoFtsWork()
    {
        using var scope = new TemporaryDatabaseScope();
        await using var connection = await SeedAsync(scope);
        var before = await FtsShadow(connection);

        await Exec(connection, "UPDATE clips SET content = content, source_app = source_app, source_url = source_url, ocr_text = ocr_text, source_window_title = source_window_title WHERE id = 1;");

        Assert.Equal(before, await FtsShadow(connection));
    }

    [Theory]
    [InlineData("content", "'delta echo foxtrot'", "delta", "alpha")]
    [InlineData("source_app", "'wordpad.exe'", "wordpad", "notepad")]
    [InlineData("source_url", "'https://example.com/zulu'", "zulu", null)]
    [InlineData("ocr_text", "'scanned yankee'", "yankee", null)]
    [InlineData("source_window_title", "'Window xray'", "xray", null)]
    public async Task ChangingAnIndexedColumn_StillReindexesIt(string column, string value, string newTerm, string? oldTerm)
    {
        using var scope = new TemporaryDatabaseScope();
        await using var connection = await SeedAsync(scope);

        await Exec(connection, $"UPDATE clips SET {column} = {value} WHERE id = 1;");

        Assert.Equal(1, await Matches(connection, newTerm));
        if (oldTerm is not null)
        {
            Assert.Equal(0, await Matches(connection, oldTerm));
        }

        await Exec(connection, "INSERT INTO clips_fts(clips_fts, rank) VALUES('integrity-check', 1);");
    }

    [Fact]
    public async Task ClearingAnIndexedColumnToNull_RemovesItsTerms()
    {
        using var scope = new TemporaryDatabaseScope();
        await using var connection = await SeedAsync(scope);

        await Exec(connection, "UPDATE clips SET source_app = NULL WHERE id = 1;");

        Assert.Equal(0, await Matches(connection, "notepad"));
        await Exec(connection, "INSERT INTO clips_fts(clips_fts, rank) VALUES('integrity-check', 1);");
    }
}