using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Database;

namespace Clipthrough.Services;

public sealed class SearchHistoryService : ISearchHistoryService
{
    private const int MaxHistoryEntries = 50;

    private readonly SqliteConnectionFactory _connectionFactory;

    public SearchHistoryService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task SaveSearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var trimmed = query.Trim();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var upsertCommand = connection.CreateCommand())
        {
            upsertCommand.CommandText = """
                INSERT INTO search_history (query, used_at)
                VALUES ($query, $usedAt)
                ON CONFLICT(query) DO UPDATE SET used_at = $usedAt;
                """;
            upsertCommand.Parameters.AddWithValue("$query", trimmed);
            upsertCommand.Parameters.AddWithValue("$usedAt", now);
            await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Prune old entries beyond the limit
        await using var pruneCommand = connection.CreateCommand();
        pruneCommand.CommandText = """
            DELETE FROM search_history
            WHERE id NOT IN (
                SELECT id FROM search_history
                ORDER BY used_at DESC
                LIMIT $limit
            );
            """;
        pruneCommand.Parameters.AddWithValue("$limit", MaxHistoryEntries);
        await pruneCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetRecentSearchesAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT query FROM search_history
            ORDER BY used_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM search_history;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
