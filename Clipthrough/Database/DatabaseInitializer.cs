using System.Threading;
using System.Threading.Tasks;
using AvaloniaApplication1.Services;
using Microsoft.Data.Sqlite;

namespace AvaloniaApplication1.Database;

public sealed class DatabaseInitializer
{
    private const string Schema = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS clips (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            content      TEXT,
            content_type TEXT NOT NULL,
            source_app   TEXT,
            hash         TEXT NOT NULL,
            is_favorite  INTEGER NOT NULL DEFAULT 0,
            is_sensitive INTEGER NOT NULL DEFAULT 0,
            captured_at  TEXT NOT NULL,
            byte_size    INTEGER NOT NULL DEFAULT 0
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS clips_fts USING fts5(
            content,
            source_app,
            content='clips',
            content_rowid='id',
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER IF NOT EXISTS clips_ai AFTER INSERT ON clips BEGIN
            INSERT INTO clips_fts(rowid, content, source_app)
            VALUES (new.id, new.content, new.source_app);
        END;

        CREATE TRIGGER IF NOT EXISTS clips_ad AFTER DELETE ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content, source_app)
            VALUES ('delete', old.id, old.content, old.source_app);
        END;

        CREATE TRIGGER IF NOT EXISTS clips_au AFTER UPDATE ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content, source_app)
            VALUES ('delete', old.id, old.content, old.source_app);
            INSERT INTO clips_fts(rowid, content, source_app)
            VALUES (new.id, new.content, new.source_app);
        END;

        CREATE INDEX IF NOT EXISTS idx_clips_captured_at ON clips(captured_at DESC);
        CREATE INDEX IF NOT EXISTS idx_clips_content_type ON clips(content_type);
        CREATE INDEX IF NOT EXISTS idx_clips_is_favorite ON clips(is_favorite) WHERE is_favorite = 1;
        CREATE INDEX IF NOT EXISTS idx_clips_is_sensitive ON clips(is_sensitive) WHERE is_sensitive = 1;
        CREATE INDEX IF NOT EXISTS idx_clips_hash ON clips(hash);

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

        foreach (var rule in _sensitivityService.GetDefaultRules())
        {
            await using var ruleCommand = connection.CreateCommand();
            ruleCommand.CommandText = """
                INSERT INTO sensitivity_rules (name, pattern, severity, is_enabled, is_builtin)
                VALUES ($name, $pattern, $severity, 1, 1)
                ON CONFLICT(name) DO UPDATE SET
                    pattern = excluded.pattern,
                    severity = excluded.severity,
                    is_builtin = 1;
                """;
            ruleCommand.Parameters.AddWithValue("$name", rule.Name);
            ruleCommand.Parameters.AddWithValue("$pattern", rule.Pattern);
            ruleCommand.Parameters.AddWithValue("$severity", rule.Severity);
            await ruleCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

