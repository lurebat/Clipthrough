using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaApplication1.Database;
using AvaloniaApplication1.Models;
using Microsoft.Data.Sqlite;

namespace AvaloniaApplication1.Services;

public sealed class ClipStoreService : IClipStoreService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISensitivityService _sensitivityService;

    public ClipStoreService(SqliteConnectionFactory connectionFactory, ISensitivityService sensitivityService)
    {
        _connectionFactory = connectionFactory;
        _sensitivityService = sensitivityService;
    }

    public async Task SeedSampleDataAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM clips;";
        var existingCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (existingCount > 0)
        {
            return;
        }

        var samples = new[]
        {
            new { Content = "SELECT * FROM users WHERE email LIKE '%@corp.com' ORDER BY created_at DESC LIMIT 100;", Source = "DBeaver", Type = ContentType.Text, Favorite = true },
            new { Content = "server=prod-sql;user id=report_user;password=Sup3rSecret!;database=warehouse;", Source = "Azure Data Studio", Type = ContentType.Text, Favorite = false },
            new { Content = "AKIA0EXAMPLEKEY123456", Source = "Visual Studio Code", Type = ContentType.Text, Favorite = false },
            new { Content = "npm install AvaloniaUI.DiagnosticsSupport --prerelease", Source = "PowerShell", Type = ContentType.Text, Favorite = false },
            new { Content = "https://github.com/AvaloniaUI/Avalonia", Source = "Chrome", Type = ContentType.Text, Favorite = true },
            new { Content = "password = Tr0ub4dor&3", Source = "Rider", Type = ContentType.Text, Favorite = false },
            new { Content = "Quarterly customer call notes and follow-up actions.", Source = "Notion", Type = ContentType.Text, Favorite = false },
            new { Content = "Files copied: Budget.xlsx; Strategy.pptx; Notes.docx", Source = "Explorer", Type = ContentType.Files, Favorite = false },
            new { Content = "RTF snippet copied from Outlook signature block", Source = "Outlook", Type = ContentType.RichText, Favorite = false },
            new { Content = "Image placeholder: screenshot captured from design review", Source = "Snipping Tool", Type = ContentType.Image, Favorite = false },
        };

        foreach (var sample in samples)
        {
            await InsertClipAsync(sample.Content, sample.Type, sample.Source, sample.Favorite, cancellationToken);
        }
    }

    public async Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var hasSearch = !string.IsNullOrWhiteSpace(filters.SearchText);
        var fromClause = hasSearch
            ? "FROM clips c JOIN clips_fts ON clips_fts.rowid = c.id"
            : "FROM clips c";
        var whereClauses = BuildWhereClauses(filters, hasSearch);
        var whereClause = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        var orderClause = hasSearch
            ? "ORDER BY bm25(clips_fts), c.captured_at DESC"
            : "ORDER BY c.captured_at DESC";

        var items = new List<ClipEntry>();

        await using (var queryCommand = connection.CreateCommand())
        {
            queryCommand.CommandText = $"""
                SELECT c.id,
                       c.content,
                       c.content_type,
                       c.source_app,
                       c.hash,
                       c.is_favorite,
                       c.is_sensitive,
                       c.captured_at,
                       c.byte_size
                {fromClause}
                {whereClause}
                {orderClause}
                LIMIT $limit OFFSET $offset;
                """;
            AddSearchParameters(queryCommand, filters, hasSearch);

            await using var reader = await queryCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadClip(reader));
            }
        }

        await LoadSensitivityMatchesAsync(connection, items, cancellationToken);

        var totalMatchingCount = await ExecuteCountAsync(connection, $"SELECT COUNT(*) {fromClause} {whereClause};", filters, hasSearch, cancellationToken);
        var totalClipCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips;", cancellationToken);
        var sensitiveClipCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips WHERE is_sensitive = 1;", cancellationToken);
        var lastCapturedAt = await ExecuteScalarStringAsync(connection, "SELECT MAX(captured_at) FROM clips;", cancellationToken);

        return new ClipSearchResult
        {
            Items = items,
            TotalMatchingCount = totalMatchingCount,
            TotalClipCount = totalClipCount,
            SensitiveClipCount = sensitiveClipCount,
            LastCapturedAt = ParseTimestamp(lastCapturedAt),
        };
    }

    public async Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET is_favorite = $isFavorite WHERE id = $id;";
        command.Parameters.AddWithValue("$id", clipId);
        command.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(long clipId, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM clips WHERE id = $id;";
        command.Parameters.AddWithValue("$id", clipId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertClipAsync(string content, ContentType contentType, string? sourceApp, bool isFavorite, CancellationToken cancellationToken)
    {
        var hash = ComputeHash(contentType, content);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var existingCommand = connection.CreateCommand();
        existingCommand.CommandText = "SELECT id FROM clips WHERE hash = $hash LIMIT 1;";
        existingCommand.Parameters.AddWithValue("$hash", hash);
        var existingId = await existingCommand.ExecuteScalarAsync(cancellationToken);
        if (existingId is not null && existingId != DBNull.Value)
        {
            return;
        }

        var matches = _sensitivityService.Scan(content);
        var capturedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var byteSize = Encoding.UTF8.GetByteCount(content);

        using var transaction = (SqliteTransaction)connection.BeginTransaction();

        long clipId;
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO clips (content, content_type, source_app, hash, is_favorite, is_sensitive, captured_at, byte_size)
                VALUES ($content, $contentType, $sourceApp, $hash, $isFavorite, $isSensitive, $capturedAt, $byteSize);
                SELECT last_insert_rowid();
                """;
            insertCommand.Parameters.AddWithValue("$content", content);
            insertCommand.Parameters.AddWithValue("$contentType", contentType.ToStorageValue());
            insertCommand.Parameters.AddWithValue("$sourceApp", (object?)sourceApp ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$hash", hash);
            insertCommand.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$isSensitive", matches.Count > 0 ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$capturedAt", capturedAt);
            insertCommand.Parameters.AddWithValue("$byteSize", byteSize);
            clipId = (long)(await insertCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        foreach (var match in matches)
        {
            var ruleId = await EnsureRuleAsync(connection, transaction, match, cancellationToken);

            await using var matchCommand = connection.CreateCommand();
            matchCommand.Transaction = transaction;
            matchCommand.CommandText = "INSERT OR IGNORE INTO clip_sensitivity_matches (clip_id, rule_id) VALUES ($clipId, $ruleId);";
            matchCommand.Parameters.AddWithValue("$clipId", clipId);
            matchCommand.Parameters.AddWithValue("$ruleId", ruleId);
            await matchCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static List<string> BuildWhereClauses(ClipSearchFilters filters, bool hasSearch)
    {
        var clauses = new List<string>();

        if (hasSearch)
        {
            clauses.Add("clips_fts MATCH $search");
        }

        if (filters.ContentType is not null)
        {
            clauses.Add("c.content_type = $contentType");
        }

        if (filters.FavoritesOnly)
        {
            clauses.Add("c.is_favorite = 1");
        }

        if (filters.SensitiveOnly)
        {
            clauses.Add("c.is_sensitive = 1");
        }

        return clauses;
    }

    private static void AddSearchParameters(SqliteCommand command, ClipSearchFilters filters, bool hasSearch)
    {
        if (hasSearch)
        {
            command.Parameters.AddWithValue("$search", BuildFtsExpression(filters.SearchText));
        }

        if (filters.ContentType is { } contentType)
        {
            command.Parameters.AddWithValue("$contentType", contentType.ToStorageValue());
        }

        command.Parameters.AddWithValue("$limit", filters.Limit);
        command.Parameters.AddWithValue("$offset", filters.Offset);
    }

    private static string BuildFtsExpression(string searchText)
    {
        var tokens = searchText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => token.Replace("\"", "\"\"", StringComparison.Ordinal))
            .Select(static token => $"\"{token}\"*");

        var expression = string.Join(" AND ", tokens);
        return string.IsNullOrWhiteSpace(expression) ? "*" : expression;
    }

    private static ClipEntry ReadClip(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
        ContentType = ContentTypeExtensions.FromStorageValue(reader.GetString(2)),
        SourceApp = reader.IsDBNull(3) ? null : reader.GetString(3),
        Hash = reader.GetString(4),
        IsFavorite = reader.GetInt64(5) == 1,
        IsSensitive = reader.GetInt64(6) == 1,
        CapturedAt = ParseTimestamp(reader.GetString(7)) ?? DateTimeOffset.UtcNow,
        ByteSize = reader.GetInt64(8),
    };

    private async Task LoadSensitivityMatchesAsync(SqliteConnection connection, IList<ClipEntry> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>();
        for (var index = 0; index < items.Count; index++)
        {
            var parameterName = $"$id{index}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, items[index].Id);
        }

        command.CommandText = $"""
            SELECT csm.clip_id, sr.id, sr.name, sr.severity
            FROM clip_sensitivity_matches csm
            JOIN sensitivity_rules sr ON sr.id = csm.rule_id
            WHERE csm.clip_id IN ({string.Join(", ", parameterNames)});
            """;

        var lookup = items.ToDictionary(static item => item.Id, static _ => new List<SensitivityMatch>());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var clipId = reader.GetInt64(0);
            lookup[clipId].Add(new SensitivityMatch
            {
                RuleId = reader.GetInt64(1),
                RuleName = reader.GetString(2),
                Severity = reader.GetString(3),
            });
        }

        foreach (var item in items)
        {
            item.SensitivityMatches = lookup[item.Id];
        }
    }

    private static async Task<int> ExecuteCountAsync(SqliteConnection connection, string sql, ClipSearchFilters filters, bool hasSearch, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (hasSearch)
        {
            command.Parameters.AddWithValue("$search", BuildFtsExpression(filters.SearchText));
        }

        if (filters.ContentType is { } contentType)
        {
            command.Parameters.AddWithValue("$contentType", contentType.ToStorageValue());
        }

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteScalarIntAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ExecuteScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is DBNull or null ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static string ComputeHash(ContentType contentType, string content)
    {
        var input = $"{contentType.ToStorageValue()}::{content}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private static async Task<long> EnsureRuleAsync(SqliteConnection connection, SqliteTransaction transaction, SensitivityMatch match, CancellationToken cancellationToken)
    {
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO sensitivity_rules (name, pattern, severity, is_enabled, is_builtin)
                VALUES ($name, $pattern, $severity, 1, 1)
                ON CONFLICT(name) DO UPDATE SET severity = excluded.severity;
                """;
            insertCommand.Parameters.AddWithValue("$name", match.RuleName);
            insertCommand.Parameters.AddWithValue("$pattern", match.RuleName);
            insertCommand.Parameters.AddWithValue("$severity", match.Severity);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT id FROM sensitivity_rules WHERE name = $name;";
        idCommand.Parameters.AddWithValue("$name", match.RuleName);
        return (long)(await idCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }
}

