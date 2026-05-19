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
            last_pasted_at TEXT,
            pinned_at    TEXT,
            ocr_text     TEXT,
            ocr_status   TEXT,
            ocr_attempted_at TEXT,
            ocr_error    TEXT,
            source_clip_id INTEGER,
            transform_kind TEXT,
            import_kind  TEXT
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS clips_fts USING fts5(
            content,
            source_app,
            source_window_title,
            source_url,
            ocr_text,
            content='clips',
            content_rowid='id',
            tokenize='trigram'
        );

        CREATE TRIGGER IF NOT EXISTS clips_ai AFTER INSERT ON clips BEGIN
            INSERT INTO clips_fts(rowid, content, source_app, source_window_title, source_url, ocr_text)
            VALUES (new.id, new.content, new.source_app, new.source_window_title, new.source_url, new.ocr_text);
        END;

        CREATE TRIGGER IF NOT EXISTS clips_ad AFTER DELETE ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content, source_app, source_window_title, source_url, ocr_text)
            VALUES ('delete', old.id, old.content, old.source_app, old.source_window_title, old.source_url, old.ocr_text);
        END;

        CREATE TRIGGER IF NOT EXISTS clips_au AFTER UPDATE ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content, source_app, source_window_title, source_url, ocr_text)
            VALUES ('delete', old.id, old.content, old.source_app, old.source_window_title, old.source_url, old.ocr_text);
            INSERT INTO clips_fts(rowid, content, source_app, source_window_title, source_url, ocr_text)
            VALUES (new.id, new.content, new.source_app, new.source_window_title, new.source_url, new.ocr_text);
        END;

        CREATE INDEX IF NOT EXISTS idx_clips_captured_at ON clips(captured_at DESC);
        CREATE INDEX IF NOT EXISTS idx_clips_content_type ON clips(content_type);
        CREATE INDEX IF NOT EXISTS idx_clips_is_favorite ON clips(is_favorite) WHERE is_favorite = 1;
        CREATE INDEX IF NOT EXISTS idx_clips_is_sensitive ON clips(is_sensitive) WHERE is_sensitive = 1;

        CREATE INDEX IF NOT EXISTS idx_clips_default_order ON clips(
            (pinned_at IS NULL),
            pinned_at DESC,
            COALESCE(last_copied_at, captured_at) DESC,
            id DESC
        );
        CREATE INDEX IF NOT EXISTS idx_clips_paste_count ON clips(paste_count DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_clips_byte_size ON clips(byte_size DESC, id DESC);

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

    /// <summary>
    /// Bump this constant whenever any of the schema-evolution helpers below
    /// (Ensure*Columns / Backfill* / DeduplicateClipsByHash /
    /// EnsureUniqueClipHashIndex / RebuildClipSearchIndex) gains new work that
    /// needs to run on existing databases. On every startup we compare against
    /// the value stored in <c>app_metadata.schema_version</c>; if the stored
    /// value matches, all the migration helpers are skipped (they're idempotent
    /// no-ops on a current database but still pay several SQLite round trips
    /// each, which adds up to ~800ms on a cold OS file cache).
    /// </summary>
    private const int CurrentSchemaVersion = 3;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISensitivityService _sensitivityService;

    public DatabaseInitializer(SqliteConnectionFactory connectionFactory, ISensitivityService sensitivityService)
    {
        _connectionFactory = connectionFactory;
        _sensitivityService = sensitivityService;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        TraceStep(sw, "connection-opened");

        // WAL mode allows concurrent readers during writes — critical for responsiveness.
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken);

        await VerifyIntegrityAsync(connection, cancellationToken);
        TraceStep(sw, "integrity-check");

        await MigrateFtsSchemaIfNeededAsync(connection, cancellationToken);
        TraceStep(sw, "fts-schema-migrate");

        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = Schema;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        TraceStep(sw, "schema-ddl");

        // Schema-version gate. On an established DB this short-circuits all
        // the Ensure*/Backfill* helpers (each of which costs at least one
        // round trip and a PRAGMA table_info read) and goes straight to the
        // sensitivity-rules seed. New installs and freshly-imported legacy
        // databases stamp 0, so they still run the full path.
        var storedVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
        TraceStep(sw, $"version-read (stored={storedVersion} current={CurrentSchemaVersion})");

        if (storedVersion < CurrentSchemaVersion)
        {
            await EnsureClipAggregationColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-aggregation-columns");
            await EnsureClipPayloadColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-payload-columns");
            await EnsureClipTrackingColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-tracking-columns");
            await EnsureClipPinningColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-pinning-columns");
            await EnsureClipOcrColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-ocr-columns");
            await EnsureClipLineageColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-lineage-columns");
            await EnsureClipEmbeddingSchemaAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-embedding-schema");
            await BackfillClipAggregationColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "backfill-aggregation");
            await BackfillClipPayloadColumnsAsync(connection, cancellationToken);
            TraceStep(sw, "backfill-payload");
            await DeduplicateClipsByHashAsync(connection, cancellationToken);
            TraceStep(sw, "dedupe-by-hash");
            await EnsureUniqueClipHashIndexAsync(connection, cancellationToken);
            TraceStep(sw, "ensure-unique-hash-index");
            await RebuildClipSearchIndexAsync(connection, cancellationToken);
            TraceStep(sw, "rebuild-search-index");
            await WriteSchemaVersionAsync(connection, CurrentSchemaVersion, cancellationToken);
            TraceStep(sw, $"version-write ({CurrentSchemaVersion})");
        }
        else
        {
            TraceStep(sw, "schema-up-to-date (migrations skipped)");
        }

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
        TraceStep(sw, "sensitivity-rules-seeded");

        await _sensitivityService.ReloadAsync(cancellationToken);
        TraceStep(sw, "sensitivity-reload (total)");
    }

    /// <summary>
    /// Reads the persisted schema version from <c>app_metadata</c>. Returns 0
    /// if the row is missing (new install or pre-versioning database) so the
    /// caller runs the full migration sequence at least once.
    /// </summary>
    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key = 'schema_version';";
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (raw is null or DBNull) return 0;
        return int.TryParse(raw.ToString(), out var v) ? v : 0;
    }

    private static async Task WriteSchemaVersionAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_metadata (key, value) VALUES ('schema_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$version", version.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void TraceStep(System.Diagnostics.Stopwatch sw, string step)
        => System.Diagnostics.Trace.TraceInformation($"[init-timing] {step} @ {sw.ElapsedMilliseconds}ms");

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

    private static async Task EnsureClipPinningColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
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

        if (!existingColumns.Contains("pinned_at"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN pinned_at TEXT;", cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_clips_pinned_at ON clips(pinned_at) WHERE pinned_at IS NOT NULL;",
            cancellationToken);
    }

    private static async Task EnsureClipOcrColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
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

        if (!existingColumns.Contains("ocr_text"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN ocr_text TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("ocr_status"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN ocr_status TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("ocr_attempted_at"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN ocr_attempted_at TEXT;", cancellationToken);
        }

        if (!existingColumns.Contains("ocr_error"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN ocr_error TEXT;", cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_clips_ocr_status ON clips(ocr_status) WHERE ocr_status IS NOT NULL;",
            cancellationToken);
    }

    private static async Task EnsureClipLineageColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
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

        if (!existingColumns.Contains("source_clip_id"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN source_clip_id INTEGER;", cancellationToken);
        }

        if (!existingColumns.Contains("transform_kind"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN transform_kind TEXT;", cancellationToken);
        }

        // import_kind marks how the clip entered Clipthrough so the UI can
        // distinguish drag-and-drop imports from real clipboard captures.
        // NULL = clipboard capture (default); "drag_drop" = imported via DnD.
        if (!existingColumns.Contains("import_kind"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN import_kind TEXT;", cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_clips_source_clip_id ON clips(source_clip_id) WHERE source_clip_id IS NOT NULL;",
            cancellationToken);
    }

    private static async Task EnsureClipEmbeddingSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
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

        if (!existingColumns.Contains("embedding_status"))
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE clips ADD COLUMN embedding_status TEXT;", cancellationToken);
        }

        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS clip_embeddings (
                clip_id       INTEGER PRIMARY KEY REFERENCES clips(id) ON DELETE CASCADE,
                model_version TEXT NOT NULL,
                dimensions    INTEGER NOT NULL,
                vector        BLOB NOT NULL,
                created_at    TEXT NOT NULL
            );
            """, cancellationToken);

        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_clips_embedding_status ON clips(embedding_status) WHERE embedding_status IS NOT NULL;",
            cancellationToken);

        // Stale reclaim: any clip marked 'processing' from a prior crashed run goes back to 'pending'.
        await ExecuteNonQueryAsync(
            connection,
            "UPDATE clips SET embedding_status = 'pending' WHERE embedding_status = 'processing';",
            cancellationToken);
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

    /// <summary>
    /// Detects if the existing FTS table has an older (2- or 4-column) schema
    /// or an older tokenizer (e.g. unicode61) and drops it along with its
    /// triggers so the Schema DDL can recreate them with the current 5-column
    /// schema and the trigram tokenizer (which supports substring matching).
    /// </summary>
    private static async Task MigrateFtsSchemaIfNeededAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Check if the FTS table exists at all
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='clips_fts';";
        var exists = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(cancellationToken)) > 0;
        if (!exists)
        {
            return;
        }

        // Check column count by querying the FTS table's content definition
        var columnCount = 0;
        await using (var colCommand = connection.CreateCommand())
        {
            colCommand.CommandText = "PRAGMA table_info(clips_fts);";
            await using var reader = await colCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columnCount++;
            }
        }

        // Inspect the stored CREATE statement to determine the tokenizer.
        string storedSql = string.Empty;
        await using (var sqlCommand = connection.CreateCommand())
        {
            sqlCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='clips_fts';";
            var raw = await sqlCommand.ExecuteScalarAsync(cancellationToken);
            storedSql = raw as string ?? string.Empty;
        }

        var hasTrigramTokenizer = storedSql.Contains("trigram", StringComparison.OrdinalIgnoreCase);

        // The current schema has 5 content columns and uses the trigram tokenizer.
        // Older versions had 2 or 4 content columns, or used unicode61.
        if (columnCount >= 5 && hasTrigramTokenizer)
        {
            return;
        }

        // Drop old triggers and FTS table so they're recreated with the new schema/tokenizer.
        await ExecuteNonQueryAsync(connection, "DROP TRIGGER IF EXISTS clips_ai;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "DROP TRIGGER IF EXISTS clips_ad;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "DROP TRIGGER IF EXISTS clips_au;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "DROP TABLE IF EXISTS clips_fts;", cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Runs <c>PRAGMA quick_check</c> immediately after the connection opens so we
    /// detect a structurally corrupt database before any migration step starts
    /// rewriting pages. <c>quick_check</c> is much faster than
    /// <c>integrity_check</c> and skips per-row content scans, so the cold-start
    /// cost is negligible on healthy databases. On a corrupt DB the migration
    /// would otherwise fail mid-way and leave the file in an even worse state.
    /// </summary>
    private static async Task VerifyIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA quick_check(5);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = reader.GetString(0);
                if (!string.Equals(row, "ok", StringComparison.Ordinal))
                {
                    problems.Add(row);
                }
            }
        }

        if (problems.Count == 0)
        {
            return;
        }

        var summary = string.Join("; ", problems.Take(5));
        if (problems.Count > 5)
        {
            summary += $" (+{problems.Count - 5} more)";
        }
        throw new InvalidOperationException(
            $"Database integrity check failed: {summary}. The file is corrupted; restore a backup or contact support.");
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

