using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Database;

public sealed class DatabaseInitializer
{
    private const string Schema = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS clips (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            content      TEXT,
            content_bytes BLOB,
            content_type TEXT NOT NULL,
            content_format TEXT NOT NULL DEFAULT 'text',
            source_app   TEXT,
            source_app_path TEXT,
            source_app_icon BLOB,
            hash         TEXT NOT NULL,
            is_favorite  INTEGER NOT NULL DEFAULT 0,
            is_sensitive INTEGER NOT NULL DEFAULT 0,
            captured_at  TEXT NOT NULL,
            copy_count   INTEGER NOT NULL DEFAULT 1,
            first_copied_at TEXT NOT NULL,
            last_copied_at  TEXT NOT NULL,
            byte_size    INTEGER NOT NULL DEFAULT 0,
            image_width  INTEGER,
            image_height INTEGER,
            source_window_title TEXT,
            source_url   TEXT,
            is_pasted    INTEGER NOT NULL DEFAULT 0,
            paste_count  INTEGER NOT NULL DEFAULT 0,
            last_pasted_at TEXT
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS clips_fts USING fts5(
            content,
            source_app,
            source_window_title,
            source_url,
            content='clips',
            content_rowid='id',
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER IF NOT EXISTS clips_ai AFTER INSERT ON clips BEGIN
            INSERT INTO clips_fts(rowid, content, source_app, source_window_title, source_url)
            VALUES (new.id, new.content, new.source_app, new.source_window_title, new.source_url);
        END;

        CREATE TRIGGER IF NOT EXISTS clips_ad AFTER DELETE ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content, source_app, source_window_title, source_url)
            VALUES ('delete', old.id, old.content, old.source_app, old.source_window_title, old.source_url);
        END;

        CREATE TRIGGER IF NOT EXISTS clips_au AFTER UPDATE ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content, source_app, source_window_title, source_url)
            VALUES ('delete', old.id, old.content, old.source_app, old.source_window_title, old.source_url);
            INSERT INTO clips_fts(rowid, content, source_app, source_window_title, source_url)
            VALUES (new.id, new.content, new.source_app, new.source_window_title, new.source_url);
        END;

        CREATE INDEX IF NOT EXISTS idx_clips_captured_at ON clips(captured_at DESC);
        CREATE INDEX IF NOT EXISTS idx_clips_content_type ON clips(content_type);
        CREATE INDEX IF NOT EXISTS idx_clips_is_favorite ON clips(is_favorite) WHERE is_favorite = 1;
        CREATE INDEX IF NOT EXISTS idx_clips_is_sensitive ON clips(is_sensitive) WHERE is_sensitive = 1;

        CREATE TABLE IF NOT EXISTS app_metadata (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sensitivity_rules (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            name        TEXT NOT NULL,
            pattern     TEXT NOT NULL,
            severity    TEXT NOT NULL DEFAULT 'warning',
            is_enabled  INTEGER NOT NULL DEFAULT 1,
            is_builtin  INTEGER NOT NULL DEFAULT 0
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_sensitivity_rules_name ON sensitivity_rules(name);

        CREATE TABLE IF NOT EXISTS clip_sensitivity_matches (
            clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
            rule_id INTEGER NOT NULL REFERENCES sensitivity_rules(id) ON DELETE CASCADE,
            PRIMARY KEY (clip_id, rule_id)
        );

        CREATE TABLE IF NOT EXISTS search_history (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            query    TEXT NOT NULL UNIQUE,
            used_at  TEXT NOT NULL
        );
        """;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISensitivityService _sensitivityService;

    public DatabaseInitializer(SqliteConnectionFactory connectionFactory, ISensitivityService sensitivityService)
    {
        _connectionFactory = connectionFactory;
        _sensitivityService = sensitivityService;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = Schema;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureClipAggregationColumnsAsync(connection, cancellationToken);
        await EnsureClipPayloadColumnsAsync(connection, cancellationToken);
        await EnsureClipTrackingColumnsAsync(connection, cancellationToken);
        await BackfillClipAggregationColumnsAsync(connection, cancellationToken);
        await BackfillClipPayloadColumnsAsync(connection, cancellationToken);
        await DeduplicateClipsByHashAsync(connection, cancellationToken);
        await EnsureUniqueClipHashIndexAsync(connection, cancellationToken);
        await RebuildClipSearchIndexAsync(connection, cancellationToken);

        foreach (var rule in _sensitivityService.GetDefaultRules())
        {
            await using var ruleCommand = connection.CreateCommand();
            ruleCommand.CommandText = """
                INSERT INTO sensitivity_rules (name, pattern, severity, is_enabled, is_builtin)
                VALUES ($name, $pattern, $severity, 1, 1)
                ON CONFLICT(name) DO UPDATE SET
                    is_builtin = 1;
                """;
            ruleCommand.Parameters.AddWithValue("$name", rule.Name);
            ruleCommand.Parameters.AddWithValue("$pattern", rule.Pattern);
            ruleCommand.Parameters.AddWithValue("$severity", rule.Severity);
            await ruleCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await _sensitivityService.ReloadAsync(cancellationToken);
    }

    private static async Task EnsureClipAggregationColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clips);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("copy_count"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN copy_count INTEGER NOT NULL DEFAULT 1;", cancellationToken);
        }

        if (!existingColumns.Contains("first_copied_at"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN first_copied_at TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("last_copied_at"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN last_copied_at TEXT;", cancellationToken);
        }
    }

    private static async Task EnsureClipPayloadColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clips);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("content_bytes"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN content_bytes BLOB;", cancellationToken);
        }

        if (!existingColumns.Contains("source_app_path"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_app_path TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("content_format"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN content_format TEXT NOT NULL DEFAULT 'text';", cancellationToken);
        }

        if (!existingColumns.Contains("source_app_icon"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_app_icon BLOB;", cancellationToken);
        }

        if (!existingColumns.Contains("image_width"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN image_width INTEGER;", cancellationToken);
        }

        if (!existingColumns.Contains("image_height"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN image_height INTEGER;", cancellationToken);
        }
    }

    private static async Task EnsureClipTrackingColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clips);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("source_window_title"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_window_title TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("source_url"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_url TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("is_pasted"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN is_pasted INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!existingColumns.Contains("paste_count"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN paste_count INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!existingColumns.Contains("last_pasted_at"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN last_pasted_at TEXT;", cancellationToken);
        }
    }

    private static async Task BackfillClipPayloadColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, """
            UPDATE clips
            SET content_format = CASE
                WHEN content_type = 'image' THEN 'bitmap'
                WHEN content_type = 'files' THEN 'files'
                WHEN content_type = 'richtext' AND content LIKE '{\\rtf%' THEN 'rtf'
                WHEN content_type = 'richtext' THEN 'html'
                ELSE 'text'
            END
            WHERE content_format IS NULL OR TRIM(content_format) = '';
            """, cancellationToken);
    }

    private static async Task BackfillClipAggregationColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "UPDATE clips SET copy_count = 1 WHERE copy_count IS NULL OR copy_count < 1;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "UPDATE clips SET first_copied_at = captured_at WHERE first_copied_at IS NULL OR TRIM(first_copied_at) = '';", cancellationToken);
        await ExecuteNonQueryAsync(connection, "UPDATE clips SET last_copied_at = captured_at WHERE last_copied_at IS NULL OR TRIM(last_copied_at) = '';", cancellationToken);
        await ExecuteNonQueryAsync(connection, "UPDATE clips SET captured_at = last_copied_at WHERE captured_at IS NULL OR TRIM(captured_at) = '';", cancellationToken);
    }

    private static async Task DeduplicateClipsByHashAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<ClipAggregationRow>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id,
                       hash,
                       source_app,
                       is_favorite,
                       is_sensitive,
                       captured_at,
                       first_copied_at,
                       last_copied_at,
                       copy_count
                FROM clips
                ORDER BY hash, COALESCE(last_copied_at, captured_at) DESC, id DESC;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var capturedAt = ParseTimestamp(reader.IsDBNull(5) ? null : reader.GetString(5));
                rows.Add(new ClipAggregationRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3) == 1,
                    reader.GetInt64(4) == 1,
                    capturedAt,
                    ParseTimestamp(reader.IsDBNull(6) ? null : reader.GetString(6)) ?? capturedAt,
                    ParseTimestamp(reader.IsDBNull(7) ? null : reader.GetString(7)) ?? capturedAt,
                    reader.IsDBNull(8) ? 1 : Convert.ToInt32(reader.GetInt64(8), CultureInfo.InvariantCulture)));
            }
        }

        foreach (var group in rows.GroupBy(static row => row.Hash, StringComparer.Ordinal))
        {
            var duplicates = group.ToList();
            if (duplicates.Count <= 1)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var keepRow = duplicates[0];
            var fallbackTimestamp = keepRow.LastCopiedAt
                ?? keepRow.FirstCopiedAt
                ?? keepRow.CapturedAt
                ?? DateTimeOffset.UtcNow;
            var firstCopiedAt = duplicates
                .Select(static row => row.FirstCopiedAt ?? row.CapturedAt)
                .Where(static timestamp => timestamp is not null)
                .Select(static timestamp => timestamp!.Value)
                .DefaultIfEmpty(fallbackTimestamp)
                .Min();
            var lastCopiedAt = duplicates
                .Select(static row => row.LastCopiedAt ?? row.CapturedAt)
                .Where(static timestamp => timestamp is not null)
                .Select(static timestamp => timestamp!.Value)
                .DefaultIfEmpty(fallbackTimestamp)
                .Max();
            var copyCount = duplicates.Sum(static row => Math.Max(1, row.CopyCount));
            var isFavorite = duplicates.Any(static row => row.IsFavorite);
            var isSensitive = duplicates.Any(static row => row.IsSensitive);
            var sourceApp = duplicates
                .OrderByDescending(static row => row.LastCopiedAt ?? row.CapturedAt ?? DateTimeOffset.MinValue)
                .Select(static row => row.SourceApp)
                .FirstOrDefault(static source => !string.IsNullOrWhiteSpace(source));
            var duplicateIds = duplicates.Skip(1).Select(static row => row.Id).ToArray();

            using var transaction = connection.BeginTransaction();

            await using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = """
                    UPDATE clips
                    SET source_app = $sourceApp,
                        is_favorite = $isFavorite,
                        is_sensitive = $isSensitive,
                        copy_count = $copyCount,
                        first_copied_at = $firstCopiedAt,
                        last_copied_at = $lastCopiedAt,
                        captured_at = $lastCopiedAt
                    WHERE id = $id;
                    """;
                updateCommand.Parameters.AddWithValue("$id", keepRow.Id);
                updateCommand.Parameters.AddWithValue("$sourceApp", (object?)sourceApp ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
                updateCommand.Parameters.AddWithValue("$isSensitive", isSensitive ? 1 : 0);
                updateCommand.Parameters.AddWithValue("$copyCount", copyCount);
                updateCommand.Parameters.AddWithValue("$firstCopiedAt", firstCopiedAt.ToString("O", CultureInfo.InvariantCulture));
                updateCommand.Parameters.AddWithValue("$lastCopiedAt", lastCopiedAt.ToString("O", CultureInfo.InvariantCulture));
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (duplicateIds.Length > 0)
            {
                var parameterNames = new List<string>(duplicateIds.Length);

                await using (var mergeMatchesCommand = connection.CreateCommand())
                {
                    mergeMatchesCommand.Transaction = transaction;
                    mergeMatchesCommand.Parameters.AddWithValue("$keepId", keepRow.Id);
                    for (var index = 0; index < duplicateIds.Length; index++)
                    {
                        var parameterName = $"$duplicateId{index}";
                        parameterNames.Add(parameterName);
                        mergeMatchesCommand.Parameters.AddWithValue(parameterName, duplicateIds[index]);
                    }

                    mergeMatchesCommand.CommandText = $"""
                        INSERT OR IGNORE INTO clip_sensitivity_matches (clip_id, rule_id)
                        SELECT $keepId, rule_id
                        FROM clip_sensitivity_matches
                        WHERE clip_id IN ({string.Join(", ", parameterNames)});
                        """;
                    await mergeMatchesCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.Transaction = transaction;
                for (var index = 0; index < duplicateIds.Length; index++)
                {
                    deleteCommand.Parameters.AddWithValue(parameterNames[index], duplicateIds[index]);
                }

                deleteCommand.CommandText = $"DELETE FROM clips WHERE id IN ({string.Join(", ", parameterNames)});";
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task EnsureUniqueClipHashIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "DROP INDEX IF EXISTS idx_clips_hash;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "CREATE UNIQUE INDEX IF NOT EXISTS idx_clips_hash_unique ON clips(hash);", cancellationToken);
    }

    private static async Task RebuildClipSearchIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
        => await ExecuteNonQueryAsync(connection, "INSERT INTO clips_fts(clips_fts) VALUES ('rebuild');", cancellationToken);

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private sealed record ClipAggregationRow(
        long Id,
        string Hash,
        string? SourceApp,
        bool IsFavorite,
        bool IsSensitive,
        DateTimeOffset? CapturedAt,
        DateTimeOffset? FirstCopiedAt,
        DateTimeOffset? LastCopiedAt,
        int CopyCount);
}

